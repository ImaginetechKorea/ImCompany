using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PDFtoImage;
using SkiaSharp;

namespace ImOrder;

public class RequestCoordinator { public SemaphoreSlim Gate { get; } = new(1, 1); }
public record AiUploadedFile(string Id, string Name, string ContentType, long Size, string Sha256, List<string> Images, DateTime CreatedAtUtc, string? Text = null);
public record AiCustomerProposal(string Id, AiCustomerFields Fields, List<string> FileIds, DateTime CreatedAtUtc, int Version = 1);
public record AiConversationLine(string Role, string Text);
public record AiTaskInput(string Message, string SessionId, long? OrderId = null, bool AllowDrafts = false, List<string>? FileIds = null);
public record AiTaskResult(string Text, string Provider, DateTime AsOfUtc, object[] Calls, object? CustomerDraft, object[] BusinessDrafts, long? ContextOrderId, List<string> SourceFileIds);
public record AiTaskSnapshot(string Id, string Status, string Stage, DateTime StartedAtUtc, AiTaskResult? Result = null, string? Error = null);
public class AiTaskState(string id, string sessionId, string requestKey)
{
    readonly object sync = new();
    AiTaskSnapshot snapshot = new(id, "Pending", "요청을 준비하고 있습니다.", DateTime.UtcNow);
    public string SessionId { get; } = sessionId;
    public string RequestKey { get; } = requestKey;
    public CancellationTokenSource Cancellation { get; } = new();
    public AiTaskSnapshot Snapshot() { lock (sync) return snapshot; }
    public void Stage(string stage) { lock (sync) snapshot = snapshot with { Status = "Running", Stage = stage }; }
    public void Complete(AiTaskResult result) { lock (sync) snapshot = snapshot with { Status = "Completed", Stage = "완료", Result = result }; }
    public void Fail(string message, bool cancelled = false) { lock (sync) snapshot = snapshot with { Status = cancelled ? "Cancelled" : "Failed", Stage = cancelled ? "취소됨" : "실패", Error = message }; }
}
public class AiSession
{
    public List<AiConversationLine> History { get; } = [];
    public List<string> FileIds { get; } = [];
    public string? ProposalId { get; set; }
    public object Sync { get; } = new();
}
public record AiSessionSnapshot(List<AiConversationLine> History, List<string> FileIds, string? ProposalId, DateTime UpdatedAtUtc);
public class AiWorkspace(DataPaths paths)
{
    public ConcurrentDictionary<string, AiTaskState> Tasks { get; } = new();
    public ConcurrentDictionary<string, AiSession> Sessions { get; } = new();
    string Root => Path.Combine(paths.Temp, "AiWorkspace");
    static void ValidId(string id) => Guard.Require(Guid.TryParseExact(id, "N", out _), "AI 자료 식별자가 잘못되었습니다.");
    public AiSession Session(string id) => Sessions.GetOrAdd(id, key =>
    {
        Guard.Require(Guid.TryParse(key, out var parsed), "대화 식별자가 잘못되었습니다."); var path = Path.Combine(Root, "Sessions", parsed.ToString("N") + ".json"); var session = new AiSession();
        if (File.Exists(path))
        {
            var saved = BusinessService.Deserialize<AiSessionSnapshot>(File.ReadAllText(path));
            if (saved.UpdatedAtUtc > DateTime.UtcNow.AddHours(-24)) { session.History.AddRange(saved.History.TakeLast(24)); session.FileIds.AddRange(saved.FileIds.TakeLast(3)); session.ProposalId = saved.ProposalId; }
        }
        return session;
    });
    public void SaveSession(string id, AiSession session)
    {
        Guard.Require(Guid.TryParse(id, out var parsed), "대화 식별자가 잘못되었습니다."); var dir = Path.Combine(Root, "Sessions"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, parsed.ToString("N") + ".json");
        lock (session.Sync) { File.WriteAllText(path + ".new", BusinessService.Serialize(new AiSessionSnapshot(session.History.TakeLast(24).ToList(), session.FileIds.TakeLast(3).ToList(), session.ProposalId, DateTime.UtcNow))); File.Move(path + ".new", path, true); }
    }
    public string FileDirectory(string id) { ValidId(id); return Path.Combine(Root, "Files", id); }
    public string Original(AiUploadedFile f) => Path.Combine(FileDirectory(f.Id), "source" + Path.GetExtension(f.Name).ToLowerInvariant());
    public AiUploadedFile GetFile(string id)
    {
        var file = Path.Combine(FileDirectory(id), "metadata.json"); Guard.Require(File.Exists(file), "첨부파일이 만료되었습니다. 다시 첨부해 주세요.", "NOT_FOUND");
        var row = BusinessService.Deserialize<AiUploadedFile>(File.ReadAllText(file)); Guard.Require(row.Id == id && row.CreatedAtUtc > DateTime.UtcNow.AddHours(-24), "첨부파일이 만료되었습니다. 다시 첨부해 주세요."); return row;
    }
    public object PublicFile(AiUploadedFile f) => new { f.Id, f.Name, f.ContentType, f.Size, pages = f.Images.Count, isText = f.Text != null, previewUrl = $"/api/ai/files/{f.Id}/preview/0" };
    public async Task<AiUploadedFile> Upload(IFormFile file, CancellationToken ct)
    {
        Guard.Require(file.Length is > 0 and <= 10 * 1024 * 1024, "AI 첨부파일은 10MB 이하로 선택해 주세요.");
        var name = Path.GetFileName(file.FileName); Guard.Require(name == file.FileName && name.Length <= 200 && !name.Contains(".."), "파일명을 확인해 주세요.");
        var ext = Path.GetExtension(name).ToLowerInvariant(); var mime = ext switch { ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".txt" => "text/plain", _ => "" }; Guard.Require(mime != "", "PDF, PNG, JPG, TXT 파일을 첨부해 주세요.");
        var id = Guid.NewGuid().ToString("N"); var directory = FileDirectory(id); Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source" + ext); await using (var output = File.Create(source)) { await file.CopyToAsync(output, ct); }
            Guard.Require(new FileInfo(source).Length == file.Length, "첨부파일 크기를 확인할 수 없습니다."); var bytes = await File.ReadAllBytesAsync(source, ct); var images = new List<string>(); string? extractedText = null;
            if (ext == ".txt")
            {
                Guard.Require(bytes.Length <= 1024 * 1024, "텍스트 파일은 1MB 이하로 첨부해 주세요.");
                try
                {
                    if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) extractedText = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
                    else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) extractedText = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
                    else { try { extractedText = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); } catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); extractedText = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); } }
                }
                catch (DecoderFallbackException) { throw new BusinessException("VALIDATION_ERROR", "텍스트 파일 인코딩을 읽지 못했습니다. UTF-8로 저장해 주세요."); }
                Guard.Require(!string.IsNullOrWhiteSpace(extractedText) && extractedText.Length <= 100000 && !extractedText.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'), "텍스트 파일은 내용이 있어야 하며 10만 자 이하여야 합니다.");
            }
            else if (ext == ".pdf")
            {
                Guard.Require(bytes.Length >= 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-", "올바른 PDF가 아닙니다.");
                try
                {
                    int count = Conversion.GetPageCount(bytes); Guard.Require(count is > 0 and <= 5, "AI 분석용 PDF는 5페이지 이하로 첨부해 주세요.");
                    var sizes = Conversion.GetPageSizes(bytes); Guard.Require(sizes.All(s => s.Width > 0 && s.Height > 0 && s.Height / s.Width is > 0.2f and < 5f), "PDF 페이지 비율이 지원 범위를 벗어났습니다.");
                    for (int page = 0; page < count; page++) { ct.ThrowIfCancellationRequested(); var output = Path.Combine(directory, $"page-{page}.png"); Conversion.SavePng(output, bytes, page: page, options: new RenderOptions(Dpi: 160, Width: 1600, WithAspectRatio: true)); images.Add(output); }
                }
                catch (BusinessException) { throw; }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new BusinessException("VALIDATION_ERROR", "PDF를 읽지 못했습니다. 암호를 해제하거나 이미지로 첨부해 주세요."); }
            }
            else
            {
                bool signature = ext == ".png" ? bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) : bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255; Guard.Require(signature, "확장자와 이미지 내용이 일치하지 않습니다.");
                using var data = SKData.CreateCopy(bytes); using var codec = SKCodec.Create(data); Guard.Require(codec != null && codec.Info.Width > 0 && codec.Info.Height > 0 && (long)codec.Info.Width * codec.Info.Height <= 32_000_000, "이미지 크기를 확인해 주세요. 최대 3,200만 화소입니다.");
                using var bitmap = SKBitmap.Decode(codec); Guard.Require(bitmap != null, "이미지를 읽을 수 없습니다.");
                var rotated = codec!.EncodedOrigin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom; var width = rotated ? bitmap.Height : bitmap.Width; var height = rotated ? bitmap.Width : bitmap.Height; var scale = Math.Min(1f, 2400f / Math.Max(width, height));
                using var normalized = new SKBitmap(Math.Max(1,(int)(width * scale)), Math.Max(1,(int)(height * scale))); using var canvas = new SKCanvas(normalized); canvas.Clear(SKColors.White); canvas.Scale(scale);
                if (codec.EncodedOrigin == SKEncodedOrigin.RightTop) { canvas.Translate(bitmap.Height, 0); canvas.RotateDegrees(90); } else if (codec.EncodedOrigin == SKEncodedOrigin.LeftBottom) { canvas.Translate(0, bitmap.Width); canvas.RotateDegrees(270); } else if (codec.EncodedOrigin == SKEncodedOrigin.BottomRight) { canvas.Translate(bitmap.Width, bitmap.Height); canvas.RotateDegrees(180); }
                canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Linear)); var output = Path.Combine(directory, "page-0.png"); using var encoded = normalized.Encode(SKEncodedImageFormat.Png, 100); using (var stream = File.Create(output)) encoded.SaveTo(stream); images.Add(output);
            }
            var record = new AiUploadedFile(id, name, mime, file.Length, StorageService.Hash(source), images, DateTime.UtcNow, extractedText); await File.WriteAllTextAsync(Path.Combine(directory, "metadata.json"), BusinessService.Serialize(record), ct); return record;
        }
        catch { Directory.Delete(directory, true); throw; }
    }
    public AiCustomerProposal SaveProposal(AiCustomerFields fields, List<string> files)
    {
        Guard.Require(files.Count <= 3, "첨부파일은 최대 3개입니다.");
        var proposal = new AiCustomerProposal(Guid.NewGuid().ToString("N"), fields, files, DateTime.UtcNow); var directory = Path.Combine(Root, "Drafts"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, proposal.Id + ".json"), BusinessService.Serialize(proposal)); return proposal;
    }
    public AiCustomerProposal GetProposal(string id)
    {
        ValidId(id); var path = Path.Combine(Root, "Drafts", id + ".json"); Guard.Require(File.Exists(path), "거래처 제안을 찾을 수 없습니다. 다시 분석해 주세요.", "NOT_FOUND"); var p = BusinessService.Deserialize<AiCustomerProposal>(File.ReadAllText(path)); Guard.Require(p.CreatedAtUtc > DateTime.UtcNow.AddHours(-24), "거래처 제안이 만료되었습니다. 다시 분석해 주세요."); return p;
    }
    public async Task<object> ProposalView(string id, AppDb db)
    {
        var p = GetProposal(id); var number = p.Fields.BusinessNumber.Replace("-", "").Trim(); var duplicates = await db.Customers.AsNoTracking().Where(x => (number != "" && x.BusinessNumber == number) || (p.Fields.CompanyName != "" && x.CompanyName == p.Fields.CompanyName)).Select(x => new { x.Id, x.CompanyName, x.BusinessNumber }).Take(10).ToListAsync(); var receipt = await db.Receipts.FindAsync("ai-customer:" + id);
        return new { p.Id, p.Fields, p.Version, p.CreatedAtUtc, files = p.FileIds.Select(x => PublicFile(GetFile(x))), duplicates, savedCustomerId = receipt?.RecordId };
    }
    public async Task<Customer> ConfirmProposal(string id, Customer input, int expectedVersion, BusinessService service, ActorContext actor)
    {
        Guard.Require(actor.Source == "UI", "거래처 정보는 사용자 화면에서 확인 후 저장해 주세요.", "FORBIDDEN"); var p = GetProposal(id); Guard.Require(p.Version == expectedVersion, "거래처 제안이 변경되었습니다.", "VERSION_CONFLICT"); var copied = new List<string>(); bool committed = false;
        try
        {
            var customer = await service.Write(async () =>
            {
                var db = service.Db; var saved = await db.Receipts.FindAsync("ai-customer:" + id); if (saved != null) return Guard.Found(await db.Customers.FindAsync(saved.RecordId));
                var c = await service.SaveCustomerCore(input);
                foreach (var fid in p.FileIds)
                {
                    var f = GetFile(fid); var source = Original(f); Guard.Require(File.Exists(source) && StorageService.Hash(source) == f.Sha256, "원본 첨부파일 검증에 실패했습니다. 다시 첨부해 주세요.");
                    var relative = $"Attachments/{BusinessService.Today:yyyy}/Customer/{c.Id}/{Guid.NewGuid():N}{Path.GetExtension(f.Name).ToLowerInvariant()}"; var target = paths.Resolve(relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); copied.Add(target);
                    var a = new Attachment { EntityType = "Customer", EntityId = c.Id, OriginalFileName = f.Name, RelativePath = relative, ContentType = f.ContentType, FileSize = f.Size, Sha256 = f.Sha256, Status = "Active" }; db.Attachments.Add(a); await db.SaveChangesAsync(); service.Audit(a, "Attach", reason: "사용자가 AI 추출 정보를 확인하고 사업자등록증 원본 첨부를 승인함");
                }
                db.Receipts.Add(new() { Key = "ai-customer:" + id, Operation = "ConfirmAiCustomer", RecordId = c.Id }); service.Audit(c, "ApproveAiCustomer", reason: $"AI 제안 {id}을 사용자 확인 후 저장"); return c;
            });
            committed = true;
            foreach (var entry in Sessions.Where(x => x.Value.ProposalId == id))
            {
                lock (entry.Value.Sync) entry.Value.History.Add(new("system-event", "사용자가 내용을 검토하고 거래처 저장을 승인했습니다. 실제 저장된 거래처: " + BusinessService.Serialize(customer)));
                SaveSession(entry.Key, entry.Value);
            }
            return customer;
        }
        catch { if (!committed) foreach (var file in copied) if (File.Exists(file)) File.Delete(file); throw; }
    }
    public async Task<object> AttachBusinessSources(string type, long id, List<string> fileIds, BusinessService service, ActorContext actor)
    {
        Guard.Require(actor.Source == "UI" && (type is "SalesOrder" or "Fulfillment" or "Invoice" or "Payment" or "PurchaseOrder"), "이 업무에는 파일을 첨부할 수 없습니다.", "FORBIDDEN");
        Guard.Require(fileIds.Count <= 3 && fileIds.Distinct().Count() == fileIds.Count, "첨부파일은 중복 없이 최대 3개입니다."); var copied = new List<string>(); bool committed = false;
        try
        {
            var count = await service.Write(async () =>
            {
                var db = service.Db; bool exists = type switch { "SalesOrder" => await db.Orders.AnyAsync(x => x.Id == id), "Fulfillment" => await db.Fulfillments.AnyAsync(x => x.Id == id), "Invoice" => await db.Invoices.AnyAsync(x => x.Id == id), "Payment" => await db.Payments.AnyAsync(x => x.Id == id), "PurchaseOrder" => await db.PurchaseOrders.AnyAsync(x => x.Id == id), _ => false }; Guard.Require(exists, "첨부할 업무 자료가 없습니다.", "NOT_FOUND");
                int added = 0; foreach (var fileId in fileIds)
                {
                    var key = $"ai-source:{type}:{id}:{fileId}"; if (await db.Receipts.AnyAsync(x => x.Key == key)) continue;
                    var f = GetFile(fileId); var source = Original(f); Guard.Require(File.Exists(source) && StorageService.Hash(source) == f.Sha256, "원본 파일 검증에 실패했습니다. 다시 첨부해 주세요.");
                    var relative = $"Attachments/{BusinessService.Today:yyyy}/{type}/{id}/{Guid.NewGuid():N}{Path.GetExtension(f.Name).ToLowerInvariant()}"; var target = paths.Resolve(relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); copied.Add(target);
                    var attachment = new Attachment { EntityType = type, EntityId = id, OriginalFileName = f.Name, RelativePath = relative, ContentType = f.ContentType, FileSize = f.Size, Sha256 = f.Sha256, Status = "Active" }; db.Attachments.Add(attachment); await db.SaveChangesAsync(); service.Audit(attachment, "Attach", reason: "AI 입력안 저장 시 사용자가 원본 첨부를 선택함"); db.Receipts.Add(new() { Key = key, Operation = "AttachAiSource", RecordId = attachment.Id }); added++;
                }
                return added;
            });
            committed = true; return new { attached = count };
        }
        catch { if (!committed) foreach (var file in copied) if (File.Exists(file)) File.Delete(file); throw; }
    }
    public void Cleanup()
    {
        if (Directory.Exists(Root)) foreach (var group in new[] { "Files", "Drafts", "Sessions" })
        {
            var dir = Path.Combine(Root, group); if (!Directory.Exists(dir)) continue;
            if (group == "Files") { foreach (var item in new DirectoryInfo(dir).GetDirectories()) if (item.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24)) item.Delete(true); }
            else foreach (var item in new DirectoryInfo(dir).GetFiles("*.json")) if (item.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24)) item.Delete();
        }
        foreach (var t in Tasks.Where(x => x.Value.Snapshot().StartedAtUtc < DateTime.UtcNow.AddHours(-24)).ToList()) Tasks.TryRemove(t.Key, out _);
    }
}
