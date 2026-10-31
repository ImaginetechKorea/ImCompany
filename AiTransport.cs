using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImOrder;

public record AiCustomerFields(string CompanyName = "", string BusinessNumber = "", string CeoName = "", string BusinessType = "", string BusinessItem = "", string Address = "", string ContactName = "", string Phone = "", string Email = "", string Memo = "", string[]? Warnings = null);
public record AiRequestedTool(string Name, string ArgumentsJson);
public record AiBusinessItem(string Description, string Specification, string Quantity, string Unit, string UnitPrice, string DiscountAmount, string TaxCategory);
public record AiBusinessFields(string Kind, string CustomerName, string ProjectName, string OrderNumber, string Date, string DueDate, string InvoiceType, string SupplyAmount, string Amount, string Percentage, string Description, string WorkType, string WorkStatus, string BankName, string Reference, string Memo, string TaxMode, List<AiBusinessItem> Items, string[] Warnings, string DocumentType = "", string VatAmount = "", string TotalAmount = "", string TaxInvoiceDate = "", string TaxApprovalNo = "", string SupplierName = "", string SupplierBusinessNumber = "", string RecipientName = "", string RecipientBusinessNumber = "");
public record AiReply(string Text, AiCustomerFields? CustomerDraft, List<AiRequestedTool> ToolCalls, List<AiBusinessFields>? BusinessDrafts = null);
public record AiConnectionStatus(string Provider, bool Ready, string Message, bool SupportsFiles = true);

public class AiTransport(SettingsStore settings, DataPaths paths)
{
    public static object ReplySchema()
    {
        var props = new Dictionary<string, object>();
        foreach (var key in new[] { "companyName", "businessNumber", "ceoName", "businessType", "businessItem", "address", "contactName", "phone", "email", "memo" }) props[key] = new { type = "string" };
        props["warnings"] = new { type = "array", items = new { type = "string" } };
        var itemProps = new Dictionary<string, object>(); foreach (var key in new[] { "description", "specification", "quantity", "unit", "unitPrice", "discountAmount", "taxCategory" }) itemProps[key] = new { type = "string" };
        var businessProps = new Dictionary<string, object>(); foreach (var key in new[] { "kind", "customerName", "projectName", "orderNumber", "date", "dueDate", "invoiceType", "supplyAmount", "amount", "percentage", "description", "workType", "workStatus", "bankName", "reference", "memo", "taxMode", "documentType", "vatAmount", "totalAmount", "taxInvoiceDate", "taxApprovalNo", "supplierName", "supplierBusinessNumber", "recipientName", "recipientBusinessNumber" }) businessProps[key] = new { type = "string" };
        businessProps["items"] = new { type = "array", maxItems = 100, items = new { type = "object", additionalProperties = false, properties = itemProps, required = itemProps.Keys.ToArray() } }; businessProps["warnings"] = new { type = "array", items = new { type = "string" } };
        return new { type = "object", additionalProperties = false, properties = new { text = new { type = "string" }, customerDraft = new { type = new[] { "object", "null" }, additionalProperties = false, properties = props, required = props.Keys.ToArray() }, toolCalls = new { type = "array", maxItems = 3, items = new { type = "object", additionalProperties = false, properties = new { name = new { type = "string" }, argumentsJson = new { type = "string" } }, required = new[] { "name", "argumentsJson" } } }, businessDrafts = new { type = "array", maxItems = 10, items = new { type = "object", additionalProperties = false, properties = businessProps, required = businessProps.Keys.ToArray() } } }, required = new[] { "text", "customerDraft", "toolCalls", "businessDrafts" } };
    }
    public string Mode()
    {
        var s = settings.Read(); return s.AiConnectionMode == "Api" || s.AiConnectionMode == "Auto" && s.AiEndpoint != "" ? "Api" : "ChatGpt";
    }
    public static string? CodexPath()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(expected)) return expected;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(x => Path.Combine(x.Trim('"'), "codex.exe")).FirstOrDefault(File.Exists);
    }
    public async Task<AiConnectionStatus> Status(CancellationToken ct)
    {
        if (Mode() == "Api") { var s = settings.Read(); var ready = s.AiEndpoint != "" && s.AiModel != "" && settings.Secret("ai") != ""; return new("Api", ready, ready ? "설정한 AI API에 연결합니다." : "AI 주소, 모델, API 키를 입력해 주세요."); }
        var exe = CodexPath(); if (exe == null) return new("ChatGpt", false, "Codex 설치와 ChatGPT 로그인이 필요합니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { var (code, output) = await Run(exe, ["login", "status"], null, null, timeout.Token); bool ready = code == 0 && output.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase); return new("ChatGpt", ready, ready ? "ChatGPT 로그인 연결됨 · 별도 API 키 없이 사용" : "Codex에서 ChatGPT 로그인을 완료해 주세요."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("ChatGpt", false, "로그인 상태 확인이 지연되고 있습니다. 다시 확인해 주세요."); }
    }
    public async Task<AiReply> Generate(string instructions, List<string> images, CancellationToken ct)
    {
        var raw = Mode() == "Api" ? await Api(instructions, images, ct) : await Codex(instructions, images, ct);
        AiReply reply;
        try { reply = BusinessService.Deserialize<AiReply>(raw); }
        catch (JsonException) { throw new BusinessException("AI_UNAVAILABLE", "AI 응답을 읽지 못했습니다. 다시 요청해 주세요."); }
        Guard.Require(reply != null && reply.Text != null && reply.Text.Length <= 20000 && reply.ToolCalls != null && reply.ToolCalls.Count <= 3, "AI 응답이 허용 범위를 벗어났습니다.");
        return reply;
    }
    async Task<string> Codex(string prompt, List<string> images, CancellationToken ct)
    {
        var exe = CodexPath(); Guard.Require(exe != null, "Codex를 설치하고 ChatGPT에 로그인해 주세요. 또는 설정에서 API 연결을 선택해 주세요.", "AI_UNAVAILABLE");
        var work = Path.Combine(paths.Temp, "ai-run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var schema = Path.Combine(work, "reply-schema.json"); var result = Path.Combine(work, "reply.json");
        try
        {
            await File.WriteAllTextAsync(schema, BusinessService.Serialize(ReplySchema()), ct);
            var args = new List<string> { "exec", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--cd", work, "--color", "never", "--json", "--output-schema", schema, "--output-last-message", result };
            // The model plans business operations only. The host executes a validated allowlist.
            foreach (var feature in new[] { "shell_tool", "apps", "plugins", "multi_agent", "code_mode", "code_mode_host", "hooks", "browser_use", "computer_use", "image_generation", "skill_search", "workspace_dependencies", "goals", "sleep_tool", "view_image", "in_app_browser" }) args.AddRange(["--disable", feature]);
            args.AddRange(["--config", "web_search=\"disabled\"", "--config", "project_doc_max_bytes=0", "--config", "features.skip_host_skill_discovery=true", "--config", "suppress_unstable_features_warning=true", "--config", "model_reasoning_effort=\"low\""]);
            if (!string.IsNullOrWhiteSpace(settings.Read().ChatGptModel)) args.AddRange(["--model", settings.Read().ChatGptModel]);
            foreach (var image in images) args.AddRange(["--image", image]); args.Add("-");
            var (exitCode, _) = await Run(exe!, args, prompt, work, ct);
            Guard.Require(exitCode == 0 && File.Exists(result), "ChatGPT 요청에 실패했습니다. 로그인·사용 한도·인터넷 연결을 확인해 주세요.", "AI_UNAVAILABLE");
            Guard.Require(new FileInfo(result).Length <= 200000, "AI 응답이 너무 큽니다."); return await File.ReadAllTextAsync(result, ct);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }
    async Task<string> Api(string prompt, List<string> images, CancellationToken ct)
    {
        var s = settings.Read(); Guard.Require(s.AiEndpoint != "" && s.AiModel != "" && settings.Secret("ai") != "", "설정에서 AI API 연결을 완료해 주세요.", "AI_UNAVAILABLE");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) }; client.DefaultRequestHeaders.Authorization = new("Bearer", settings.Secret("ai"));
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt } };
        foreach (var image in images) content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(image, ct)) } });
        using var response = await client.PostAsJsonAsync(s.AiEndpoint, new { model = s.AiModel, messages = new[] { new { role = "user", content } }, response_format = new { type = "json_schema", json_schema = new { name = "imorder_assistant", strict = true, schema = ReplySchema() } } }, BusinessService.Json, ct);
        Guard.Require(response.IsSuccessStatusCode, "AI 서비스 요청이 실패했습니다. 이미지·구조화 응답을 지원하는 모델인지 확인해 주세요.", "AI_UNAVAILABLE");
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct); var text = body?["choices"]?[0]?["message"]?["content"]?.GetValue<string>(); Guard.Require(!string.IsNullOrWhiteSpace(text), "AI가 읽을 수 있는 응답을 반환하지 않았습니다.", "AI_UNAVAILABLE"); return text!;
    }
    static async Task<(int Code, string Output)> Run(string exe, IEnumerable<string> arguments, string? input, string? cwd, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        if (cwd != null) start.WorkingDirectory = cwd; foreach (var arg in arguments) start.ArgumentList.Add(arg);
        // Do not inherit another Codex turn's request metadata into this app's independent session.
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("CODEX_THREAD_") || x.StartsWith("CODEX_TURN_")).ToList()) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new BusinessException("AI_UNAVAILABLE", "ChatGPT 연결 프로그램을 실행할 수 없습니다.");
        try
        {
            var output = ReadBounded(process.StandardOutput, ct); var error = ReadBounded(process.StandardError, ct);
            if (input != null) await process.StandardInput.WriteAsync(input.AsMemory(), ct); process.StandardInput.Close(); await process.WaitForExitAsync(ct); return (process.ExitCode, await output + await error);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0) if (result.Length < 200000) result.Append(buffer, 0, Math.Min(count, 200000 - result.Length));
        return result.ToString();
    }
}
