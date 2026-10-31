using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImOrder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

var contentRoot = File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html")) ? AppContext.BaseDirectory : Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(contentRoot, "wwwroot", "index.html")))
{
    Console.Error.WriteLine("프로그램 화면 파일을 찾을 수 없습니다. 배포 ZIP을 모두 압축 해제한 폴더 또는 publish 폴더에서 ImCompany.exe를 실행하세요.");
    Environment.ExitCode = 1;
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
var port = int.TryParse(builder.Configuration["port"], out var configuredPort) ? configuredPort : 5274;
Guard.Require(port is >= 1024 and <= 65535, "포트 범위를 확인해 주세요.");
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var paths = new DataPaths(builder.Configuration["data-root"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImCompany", "ImOrder"));
using var instance = new Mutex(true, "Local\\ImOrder-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..20], out var firstInstance);
if (!firstInstance) { if (!args.Contains("--no-browser")) Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true }); return; }
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<WriteGate>();
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={paths.Database};Foreign Keys=True;Pooling=False;Default Timeout=10"));
builder.Services.AddScoped<ActorContext>();
builder.Services.AddScoped<BusinessService>();
builder.Services.AddScoped<PurchaseService>();
builder.Services.AddScoped<StorageService>();
builder.Services.AddScoped<ToolService>();
builder.Services.AddScoped<AiService>();
builder.Services.AddSingleton<RequestCoordinator>();
builder.Services.AddSingleton<AiWorkspace>();
builder.Services.AddSingleton<AiTaskRunner>();
builder.Services.AddScoped<AiTransport>();
builder.Services.AddScoped<AutomationService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 52L * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.MaxDepth = 32; });
builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<McpTools>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureCreatedAsync();
    SchemaUpgrade.EnsureCustomerPriority(paths.Database);
    await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS SchemaVersions (Version INTEGER PRIMARY KEY, AppliedAtUtc TEXT NOT NULL); INSERT OR IGNORE INTO SchemaVersions VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ','now')); PRAGMA journal_mode=WAL;");
    using var cmd = db.Database.GetDbConnection().CreateCommand(); await db.Database.OpenConnectionAsync(); cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersions"; Guard.Require(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1, "지원하지 않는 데이터베이스 버전입니다."); await db.Database.CloseConnectionAsync();
    var store = scope.ServiceProvider.GetRequiredService<SettingsStore>(); if (!File.Exists(paths.Resolve("settings.json"))) store.Save(new(DefaultPolicy: new()));
    if (store.Secret("mcp") == "") store.SetSecret("mcp", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
}
var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var requests = app.Services.GetRequiredService<RequestCoordinator>().Gate;
app.Services.GetRequiredService<AiWorkspace>().Cleanup();
app.Use(async (ctx, next) =>
{
    var trace = Guid.NewGuid().ToString("N"); ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    ctx.Response.Headers.CacheControl = "no-store";
    bool locked = false;
    try
    {
        Guard.Require(ctx.Connection.RemoteIpAddress == null || IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress), "로컬 PC에서만 접근할 수 있습니다.", "FORBIDDEN");
        Guard.Require(ctx.Request.Host.Host is "127.0.0.1" or "localhost" && ctx.Request.Host.Port == port, "허용되지 않은 호스트입니다.", "FORBIDDEN");
        var origin = ctx.Request.Headers.Origin.ToString(); Guard.Require(origin == "" || origin == $"http://127.0.0.1:{port}" || origin == $"http://localhost:{port}", "허용되지 않은 요청 출처입니다.", "FORBIDDEN");
        if (ctx.Request.Path.StartsWithSegments("/mcp"))
        {
            var store = ctx.RequestServices.GetRequiredService<SettingsStore>(); Guard.Require(store.Read().McpEnabled && ctx.Request.Headers.Authorization == "Bearer " + store.Secret("mcp"), "MCP가 꺼져 있거나 인증 토큰이 올바르지 않습니다.", "FORBIDDEN");
        }
        else if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Method != "GET") Guard.Require(ctx.Request.Headers["X-ImOrder-Token"] == csrf, "사용자 화면에서 다시 시도해 주세요.", "FORBIDDEN");
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/mcp")) { await requests.WaitAsync(ctx.RequestAborted); locked = true; }
        ctx.RequestServices.GetRequiredService<ActorContext>().CorrelationId = trace; await next(ctx);
    }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        var code = ex is BusinessException b ? b.Code : ex is JsonException or BadHttpRequestException or FormatException or OverflowException or InvalidDataException ? "VALIDATION_ERROR" : "INTERNAL_ERROR";
        ctx.Response.StatusCode = code switch { "FORBIDDEN" => 403, "NOT_FOUND" => 404, "VERSION_CONFLICT" or "DUPLICATE_REQUEST" => 409, "INTERNAL_ERROR" => 500, _ => 400 };
        if (code == "INTERNAL_ERROR") app.Logger.LogError(ex, "Request {Trace}", trace);
        await ctx.Response.WriteAsJsonAsync(new { code, message = ex is BusinessException ? ex.Message : code == "VALIDATION_ERROR" ? "입력값 또는 파일 형식을 확인해 주세요." : "처리 중 오류가 발생했습니다. 잠시 후 다시 시도해 주세요.", traceId = trace });
    }
    finally { if (locked) requests.Release(); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/bootstrap", (SettingsStore s) => new { token = csrf, settings = s.Read(), dataRoot = paths.Root, aiConfigured = s.Secret("ai") != "", version = "1.1.0" });
app.MapGet("/api/state", (BusinessService s) => s.State());
app.MapGet("/api/orders/{id:long}/financial", (long id, BusinessService s) => s.Financial(id));
app.MapPost("/api/customers", (Customer c, BusinessService s) => s.SaveCustomer(c));
app.MapPut("/api/customers/{id:long}", (long id, Customer c, BusinessService s) => s.SaveCustomer(c, id));
app.MapPost("/api/customers/{id:long}/delete", (long id, CommandMeta m, BusinessService s) => s.DeleteCustomer(id, m));
app.MapPost("/api/calculate", (DocumentInput i) => MoneyCalculator.Calculate(i.Items, i.Policy));
app.MapPost("/api/orders", (DocumentInput i, BusinessService s) => s.DirectOrder(i));
app.MapPost("/api/orders/{id:long}/close", (long id, CommandMeta m, BusinessService s) => s.CloseOrder(id, m));
app.MapPost("/api/orders/{id:long}/reopen", (long id, CommandMeta m, BusinessService s) => s.ReopenOrder(id, m));
app.MapPut("/api/orders/{id:long}/details", (long id, OrderDetailsInput i, BusinessService s) => s.UpdateOrderDetails(id, i));
app.MapPost("/api/orders/{id:long}/contract", (long id, ContractChangeInput i, BusinessService s) => s.ChangeContract(id, i));
app.MapPut("/api/invoices/{id:long}/tax-record", (long id, TaxInvoiceInput i, BusinessService s) => s.RecordTaxInvoice(id, i));
app.MapPut("/api/invoices/{id:long}", (long id, InvoiceUpdateInput i, BusinessService s) => s.UpdateInvoice(id, i));
app.MapPost("/api/invoices/{id:long}/delete", (long id, CommandMeta m, BusinessService s) => s.DeleteInvoice(id, m));
app.MapPost("/api/invoices", (InvoiceInput i, BusinessService s) => s.CreateInvoice(i));
app.MapPost("/api/invoices/{id:long}/confirm", (long id, CommandMeta m, BusinessService s) => s.ConfirmInvoice(id, m));
app.MapPut("/api/payments/{id:long}", (long id, PaymentUpdateInput i, BusinessService s) => s.UpdatePayment(id, i));
app.MapPost("/api/purchases", (PurchaseOrderInput input, PurchaseService service) => service.SaveOrder(input));
app.MapPut("/api/purchases/{id:long}", (long id, PurchaseOrderInput input, PurchaseService service) => service.SaveOrder(input, id));
app.MapPost("/api/purchases/{id:long}/receipts", (long id, PurchaseReceiptInput input, PurchaseService service) => service.AddReceipt(id, input));
app.MapPost("/api/purchases/{id:long}/bills", (long id, PurchaseBillInput input, PurchaseService service) => service.AddBill(id, input));
app.MapPost("/api/purchases/{id:long}/payments", (long id, PurchaseDisbursementInput input, PurchaseService service) => service.AddPayment(id, input));
app.MapPost("/api/purchases/cancel/{kind}/{id:long}", (string kind, long id, CommandMeta input, PurchaseService service) => service.Cancel(kind, id, input));
app.MapGet("/api/print-purchase/{id:long}", async (long id, DocumentService d) => Results.Content(await d.PurchaseHtml(id), "text/html; charset=utf-8"));
app.MapGet("/api/purchases/{id:long}/totals", (long id, PurchaseService service) => service.Totals(id));
app.MapPost("/api/payments", (PaymentInput i, BusinessService s) => s.RegisterPayment(i));
app.MapPost("/api/payments/{id:long}/project", (long id, PaymentProjectInput i, BusinessService s) => s.LinkPaymentProject(id, i));
app.MapPost("/api/payments/{id:long}/confirm", (long id, CommandMeta m, BusinessService s) => s.ConfirmPayment(id, m));
app.MapPost("/api/allocations", (AllocationInput i, BusinessService s) => s.Allocate(i));
app.MapPost("/api/allocations/{id:long}/reverse", (long id, CommandMeta m, BusinessService s) => s.Reverse(id, m));
app.MapPost("/api/cancel/{type}/{id:long}", (string type, long id, CommandMeta m, BusinessService s) => s.Cancel(type, id, m));
app.MapPost("/api/fulfillments", (Fulfillment i, BusinessService s) => s.SaveFulfillment(i));
app.MapPut("/api/fulfillments/{id:long}", (long id, Fulfillment i, BusinessService s) => s.SaveFulfillment(i, id));
app.MapGet("/api/payments/{id:long}/suggest", (long id, BusinessService s) => s.SuggestAllocation(id));
app.MapGet("/api/audit/{type}/{id:long}", async (string type, long id, AppDb db) => await db.AuditLogs.AsNoTracking().Where(x => x.EntityType == type && x.RecordId == id).OrderByDescending(x => x.Id).Take(500).ToListAsync());
app.MapPost("/api/attachments/{type}/{id:long}", async (string type, long id, HttpRequest request, StorageService s) => { var form = await request.ReadFormAsync(); return await s.Upload(type, id, Guard.Found(form.Files.GetFile("file"))); });
app.MapGet("/api/attachments/{id:long}", async (long id, AppDb db) => { var a = Guard.Found(await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Status == "Active")); return Results.File(await File.ReadAllBytesAsync(paths.Resolve(a.RelativePath)), a.ContentType, a.OriginalFileName); });
app.MapPost("/api/attachments/{id:long}/delete", (long id, CommandMeta m, StorageService s) => s.DeleteAttachment(id, m));
app.MapGet("/api/backups", () => new DirectoryInfo(paths.Backups).GetFiles("ImCompany_Excel_*.xlsx").OrderByDescending(x => x.LastWriteTimeUtc).Select(x => new { name = x.Name, size = x.Length, createdAtUtc = x.LastWriteTimeUtc }));
app.MapPost("/api/backups", async (DocumentService d, StorageService storage) => new { name = await d.SaveExcelBackup(paths.Backups, storage, paths) });
app.MapPost("/api/backups/{name}/restore", async (string name, DocumentService d, StorageService storage) => {
    Guard.Require(Path.GetFileName(name) == name && name.StartsWith("ImCompany_Excel_") && name.EndsWith(".xlsx"), "백업 파일명이 잘못되었습니다.");
    var file = Path.Combine(paths.Backups, name); Guard.Require(File.Exists(file), "백업이 없습니다.", "NOT_FOUND");
    var temp = Path.Combine(paths.Temp, "excel-restore-" + Guid.NewGuid().ToString("N") + ".zip");
    try {
        using (var archive = System.IO.Compression.ZipFile.OpenRead(file)) { var entries = archive.Entries.Where(x => x.FullName == "imcompany/restore-data.zip").ToList(); Guard.Require(entries.Count == 1 && entries[0].Length <= 10L * 1024 * 1024 * 1024, "복원 정보가 없거나 올바르지 않습니다. 원본 엑셀 백업을 선택해 주세요."); using var input = entries[0].Open(); using var output = File.Create(temp); await input.CopyToAsync(output); }
        storage.VerifyBackup(temp, Path.Combine(paths.Temp, "excel-check-" + Guid.NewGuid().ToString("N")));
        await d.SaveExcelBackup(paths.Backups, storage, paths);
        await storage.RestorePath(temp, name); return new { restored = true };
    } finally { if (File.Exists(temp)) File.Delete(temp); }
});
app.MapGet("/api/backups/{name}", (string name) => { Guard.Require(Path.GetFileName(name) == name && name.StartsWith("ImCompany_Excel_") && name.EndsWith(".xlsx"), "백업 파일명이 잘못되었습니다."); return Results.File(File.ReadAllBytes(Path.Combine(paths.Backups, name)), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name); });
app.MapPost("/api/open-data-folder", () => { Process.Start(new ProcessStartInfo("explorer.exe", paths.Root) { UseShellExecute = true }); return Results.Ok(); });
app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) => { _ = Task.Run(async () => { await Task.Delay(500); lifetime.StopApplication(); }); return new { stopped = true }; });
app.MapPut("/api/settings", async (AppSettings input, SettingsStore s, WriteGate gate) => { await gate.Semaphore.WaitAsync(); try { s.Save(input); } finally { gate.Semaphore.Release(); } return s.Read(); });
app.MapPost("/api/secrets/{name}", async (string name, SecretInput input, SettingsStore s, WriteGate gate) => { await gate.Semaphore.WaitAsync(); try { s.SetSecret(name, input.Value); } finally { gate.Semaphore.Release(); } return Results.Ok(); });
app.MapGet("/api/mcp-info", (SettingsStore s) => new { url = $"http://127.0.0.1:{port}/mcp", token = s.Secret("mcp"), tools = ToolService.Specs });
app.MapPost("/api/ai/chat", (ChatInput i, AiService ai, CancellationToken ct) => ai.Chat(i, ct));
app.MapGet("/api/ai/status", (AiTransport transport, CancellationToken ct) => transport.Status(ct));
app.MapPost("/api/ai/files", async (HttpRequest request, AiWorkspace workspace, CancellationToken ct) => { var form = await request.ReadFormAsync(ct); var uploaded = await workspace.Upload(Guard.Found(form.Files.GetFile("file")), ct); return workspace.PublicFile(uploaded); });
app.MapGet("/api/ai/files/{id}/preview/{page:int}", (string id, int page, AiWorkspace workspace) => { var f = workspace.GetFile(id); if (f.Text != null) { Guard.Require(page == 0, "페이지를 찾을 수 없습니다."); return Results.Content(f.Text, "text/plain", Encoding.UTF8); } Guard.Require(page >= 0 && page < f.Images.Count, "페이지를 찾을 수 없습니다."); return Results.File(File.ReadAllBytes(f.Images[page]), "image/png"); });
app.MapGet("/api/ai/files/{id}/original", (string id, AiWorkspace workspace) => { var f = workspace.GetFile(id); return Results.File(File.ReadAllBytes(workspace.Original(f)), f.ContentType, f.Name); });
app.MapPost("/api/ai/tasks", (AiTaskInput input, AiTaskRunner runner) => runner.Start(input));
app.MapGet("/api/ai/tasks/{id}", (string id, AiWorkspace workspace) => { Guard.Require(workspace.Tasks.TryGetValue(id, out var task), "AI 작업을 찾을 수 없습니다.", "NOT_FOUND"); return task!.Snapshot(); });
app.MapPost("/api/ai/tasks/{id}/cancel", (string id, AiWorkspace workspace) => { Guard.Require(workspace.Tasks.TryGetValue(id, out var task), "AI 작업을 찾을 수 없습니다.", "NOT_FOUND"); if (task!.Snapshot().Status is "Running" or "Pending") task.Cancellation.Cancel(); return task.Snapshot(); });
app.MapGet("/api/ai/customer-drafts/{id}", (string id, AiWorkspace workspace, AppDb db) => workspace.ProposalView(id, db));
app.MapPost("/api/ai/customer-drafts/{id}/confirm", (string id, AiCustomerConfirmation input, AiWorkspace workspace, BusinessService s, ActorContext actor) => workspace.ConfirmProposal(id, input.Customer, input.ExpectedVersion, s, actor));
app.MapPost("/api/ai/business-sources/{type}/{id:long}", (string type, long id, AiSourceInput input, AiWorkspace workspace, BusinessService s, ActorContext actor) => workspace.AttachBusinessSources(type, id, input.FileIds, s, actor));
app.MapGet("/api/ai/operations/{key}", async (string key, AppDb db) => { Guard.Require(Guid.TryParse(key, out _), "요청 식별자를 확인해 주세요."); var receipt = Guard.Found(await db.Receipts.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key)); Guard.Require(receipt.Operation is "CreateOrder" or "CreateInvoiceDraft" or "CreatePayment" or "CreateFulfillment" or "PurchaseOrderSave" or "PurchaseReceipt" or "PurchaseBill" or "PurchasePayment", "조회할 수 없는 요청입니다.", "FORBIDDEN"); return new { receipt.Operation, receipt.RecordId }; });
app.MapGet("/api/print/{type}/{id:long}", async (string type, long id, DocumentService d) => Results.Content(await d.Html(type, id), "text/html", Encoding.UTF8));
app.MapGet("/api/export/{kind}", async (string kind, DocumentService d) => Results.File(await d.Excel(kind), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"ImCompany_{kind}_{BusinessService.Today:yyyyMMdd}.xlsx"));
app.MapMcp("/mcp");
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (!args.Contains("--no-browser")) Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true });
});
// Automatic backup waits for requests so it never replaces data during an active workflow.
var backupCancellation = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(() => backupCancellation.Cancel());
_ = Task.Run(async () =>
{
    while (!backupCancellation.IsCancellationRequested)
    {
        try
        {
            await requests.WaitAsync(backupCancellation.Token);
            try
            {
                bool todayExists = new DirectoryInfo(paths.Backups).GetFiles("ImCompany_Excel_*.xlsx").Any(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(x.LastWriteTimeUtc, "Korea Standard Time")) == BusinessService.Today);
                if (!todayExists) { using var scope = app.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<DocumentService>().SaveExcelBackup(paths.Backups, scope.ServiceProvider.GetRequiredService<StorageService>(), paths); }
            }
            finally { requests.Release(); }
            await Task.Delay(TimeSpan.FromMinutes(15), backupCancellation.Token);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { app.Logger.LogError(ex, "Automatic backup failed"); await Task.Delay(TimeSpan.FromSeconds(30)); }
    }
});
await app.RunAsync();
public record SecretInput(string Value);
public record AiCustomerConfirmation(Customer Customer, int ExpectedVersion);
public record AiSourceInput(List<string> FileIds);
