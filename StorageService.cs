using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ImOrder;

public class DataPaths
{
    public string Root { get; }
    public static string DefaultRoot() => MigrateRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImCompany"));
    public static string MigrateRoot(string parent)
    {
        var current = Path.Combine(parent, "ImCompany");
        var legacy = Path.Combine(parent, "ImOrder");
        if (Directory.Exists(current))
        {
            if (!File.Exists(Path.Combine(current, "imorder.db")) && File.Exists(Path.Combine(legacy, "imorder.db"))) throw new IOException("새 저장 폴더에 업무자료가 없습니다. 기존 자료를 확인한 후 다시 실행해 주세요.");
            return current;
        }
        if (!File.Exists(Path.Combine(legacy, "imorder.db"))) return current;
        var mutexName = "Local\\ImOrder-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(legacy).ToUpperInvariant())))[..20];
        using var oldLock = new Mutex(true, mutexName, out var available);
        if (!available) throw new InvalidOperationException("이전 ImCompany 프로그램을 종료한 후 다시 실행해 주세요. 기존 자료는 변경되지 않았습니다.");
        var temporary = Path.Combine(parent, ".ImCompany-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        CopyDirectory(legacy, temporary);
        if (!File.Exists(Path.Combine(temporary, "imorder.db"))) throw new IOException("기존 업무자료를 복사하지 못했습니다. 기존 자료는 변경되지 않았습니다.");
        if (!Directory.Exists(current)) Directory.Move(temporary, current);
        return current;
    }
    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("연결된 폴더가 있어 자동 이전을 중단했습니다.");
            var target = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            CopyDirectory(directory, target);
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("연결된 파일이 있어 자동 이전을 중단했습니다.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
    public string Database => Path.Combine(Root, "imorder.db");
    public string Backups => Path.Combine(Root, "Backup");
    public string Temp => Path.Combine(Root, "Temp");
    public DataPaths(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); Directory.CreateDirectory(Backups); Directory.CreateDirectory(Temp); Directory.CreateDirectory(Path.Combine(Root, "Attachments")); }
    public string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        Guard.Require(!Path.IsPathRooted(relative) && path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "허용되지 않은 파일 경로입니다."); return path;
    }
}
public record AppSettings(string CompanyName = "ImCompany", string BusinessNumber = "", string Address = "", string Phone = "", TaxPolicy? DefaultPolicy = null, string AiEndpoint = "", string AiModel = "", bool AiDraftsEnabled = false, bool McpEnabled = false, int MaxAttachmentMb = 50, string AiConnectionMode = "Auto", string ChatGptModel = "");
public class SettingsStore(DataPaths paths)
{
    public AppSettings Read() => File.Exists(paths.Resolve("settings.json")) ? BusinessService.Deserialize<AppSettings>(File.ReadAllText(paths.Resolve("settings.json"))) : new(DefaultPolicy: new());
    public void Save(AppSettings settings)
    {
        Guard.Require(settings.MaxAttachmentMb is >= 1 and <= 50, "첨부 크기는 1~50MB로 설정해 주세요.");
        Guard.Require(settings.AiConnectionMode is "Auto" or "ChatGpt" or "Api", "AI 연결 방식을 확인해 주세요.");
        if (settings.AiEndpoint != "") Guard.Require(Uri.TryCreate(settings.AiEndpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment), "AI 주소는 자격 증명이나 쿼리가 없는 HTTPS 주소여야 합니다.");
        var temp = paths.Resolve("settings.json.new"); File.WriteAllText(temp, BusinessService.Serialize(settings)); File.Move(temp, paths.Resolve("settings.json"), true);
    }
    public void SetSecret(string name, string value)
    {
        Guard.Require(name is "ai" or "mcp", "지원하지 않는 자격 증명입니다.");
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser); File.WriteAllBytes(paths.Resolve(name + ".secret"), bytes);
    }
    public string Secret(string name)
    {
        var file = paths.Resolve(name + ".secret"); return File.Exists(file) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser)) : "";
    }
}
public record ManifestEntry(string Path, long Size, string Sha256);
public record BackupManifest(int SchemaVersion, string AppVersion, DateTime CreatedAtUtc, List<ManifestEntry> Files);
public class StorageService(DataPaths paths, SettingsStore settings, AppDb db, WriteGate gate, BusinessService business)
{
    public static bool IsBackupName(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && (name.StartsWith("ImCompany_Backup_", StringComparison.Ordinal) || name.StartsWith("ImOrder_Backup_", StringComparison.Ordinal));
    public static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    async Task Exists(string type, long id)
    {
        bool exists = type switch { "Customer" => await db.Customers.AnyAsync(x => x.Id == id), "Quotation" => await db.Quotations.AnyAsync(x => x.Id == id), "SalesOrder" => await db.Orders.AnyAsync(x => x.Id == id), "Invoice" => await db.Invoices.AnyAsync(x => x.Id == id), "Payment" => await db.Payments.AnyAsync(x => x.Id == id), "Fulfillment" => await db.Fulfillments.AnyAsync(x => x.Id == id), "PurchaseOrder" => await db.PurchaseOrders.AnyAsync(x => x.Id == id), "Asset" => await db.Assets.AnyAsync(x => x.Id == id && x.Status != "Deleted"), _ => false };
        Guard.Require(exists, "첨부 대상이 없습니다.", "NOT_FOUND");
    }
    public async Task<Attachment> Upload(string type, long id, IFormFile file)
    {
        Guard.Require(file.Length > 0 && file.Length <= settings.Read().MaxAttachmentMb * 1024L * 1024, "첨부파일 크기를 확인해 주세요. 최대 50MB입니다.");
        var original = Path.GetFileName(file.FileName); Guard.Require(original == file.FileName && !original.Contains(".."), "파일명에 경로를 사용할 수 없습니다."); var ext = Path.GetExtension(original).ToLowerInvariant();
        var mime = ext switch { ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".txt" => "text/plain", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", _ => "" }; Guard.Require(mime != "", "PDF, XLSX, DOCX, PNG, JPG, TXT만 첨부할 수 있습니다.");
        var staging = Path.Combine(paths.Temp, Guid.NewGuid() + ext);
        try
        {
            await using (var stream = File.Create(staging)) { await file.CopyToAsync(stream); }
            var info = new FileInfo(staging); Guard.Require(info.Length == file.Length, "파일 크기 검증에 실패했습니다.");
            var header = new byte[8]; using (var f = File.OpenRead(staging)) { _ = f.Read(header); }
            bool valid = ext switch { ".pdf" => Encoding.ASCII.GetString(header).StartsWith("%PDF-"), ".png" => header.SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }), ".jpg" or ".jpeg" => header[0] == 255 && header[1] == 216 && header[2] == 255, ".xlsx" or ".docx" => header[0] == 80 && header[1] == 75, ".txt" => !File.ReadAllBytes(staging).Contains((byte)0), _ => false };
            Guard.Require(valid, "확장자와 파일 내용이 일치하지 않습니다.");
            if (ext is ".docx" or ".xlsx") { using var zip = ZipFile.OpenRead(staging); Guard.Require(zip.GetEntry("[Content_Types].xml") != null && zip.GetEntry(ext == ".docx" ? "word/document.xml" : "xl/workbook.xml") != null && !zip.Entries.Any(x => x.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)), "올바른 Office 문서가 아닙니다."); }
            return await business.Write(async () =>
            {
                await Exists(type, id); var relative = $"Attachments/{BusinessService.Today:yyyy}/{type}/{id}/{Guid.NewGuid():N}{ext}";
                var row = new Attachment { EntityType = type, EntityId = id, OriginalFileName = original, RelativePath = relative, ContentType = mime, FileSize = info.Length, Sha256 = Hash(staging) }; db.Attachments.Add(row); await db.SaveChangesAsync();
                var final = paths.Resolve(relative); Directory.CreateDirectory(Path.GetDirectoryName(final)!); File.Move(staging, final); row.Status = "Active"; business.Audit(row, "Attach"); return row;
            });
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    public async Task<Attachment> DeleteAttachment(long id, CommandMeta meta) => await business.Write(async () =>
    { var a = Guard.Found(await db.Attachments.FindAsync(id)); Guard.Version(a, meta.ExpectedVersion); Guard.Require(a.Status == "Active", "유효한 첨부가 아닙니다."); var old = BusinessService.Serialize(a); a.Status = "Deleted"; a.Version++; business.Audit(a, "Delete", old, meta.Reason); return a; });
    public async Task<string> Backup(bool alreadyLocked = false, string? destinationFolder = null)
    {
        if (!alreadyLocked) await gate.Semaphore.WaitAsync();
        var stage = Path.Combine(paths.Temp, "backup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            using (var source = new SqliteConnection($"Data Source={paths.Database};Foreign Keys=True;Pooling=False"))
            using (var target = new SqliteConnection($"Data Source={Path.Combine(stage, "imorder.db")};Pooling=False")) { source.Open(); target.Open(); source.BackupDatabase(target); }
            foreach (var file in Directory.GetFiles(paths.Resolve("Attachments"), "*", SearchOption.AllDirectories)) { var dest = Path.Combine(stage, Path.GetRelativePath(paths.Root, file)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest); }
            File.WriteAllText(Path.Combine(stage, "settings.json"), BusinessService.Serialize(settings.Read()));
        }
        finally { if (!alreadyLocked) gate.Semaphore.Release(); }
        try
        {
            var entries = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(f => new ManifestEntry(Path.GetRelativePath(stage, f).Replace('\\', '/'), new FileInfo(f).Length, Hash(f))).ToList();
            File.WriteAllText(Path.Combine(stage, "manifest.json"), BusinessService.Serialize(new BackupManifest(1, "1.1.0", DateTime.UtcNow, entries)));
            var name = $"ImCompany_Backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.zip"; var destination = Path.Combine(destinationFolder ?? paths.Backups, name); ZipFile.CreateFromDirectory(stage, destination); VerifyBackup(destination, Path.Combine(paths.Temp, "verify-" + Guid.NewGuid().ToString("N")));
            // Keep the most recent backup for each of the last 30 Korean business days.
            var files = new DirectoryInfo(destinationFolder ?? paths.Backups).GetFiles("*.zip").Where(x => IsBackupName(x.Name)).OrderByDescending(x => x.LastWriteTimeUtc).ToList();
            var keep = files.GroupBy(x => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(x.LastWriteTimeUtc, "Korea Standard Time").Date).Take(30).Select(x => x.First().FullName).ToHashSet();
            if (destinationFolder == null) foreach (var file in files.Skip(1)) if (!keep.Contains(file.FullName)) file.Delete();
            return name;
        }
        finally { Directory.Delete(stage, true); }
    }
    public BackupManifest VerifyBackup(string zipPath, string stage, bool keep = false)
    {
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath); Guard.Require(zip.Entries.Count <= 100000 && zip.Entries.Sum(x => x.Length) <= 10L * 1024 * 1024 * 1024, "백업 크기 제한을 초과했습니다.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(stage, e.FullName));
                Guard.Require(target.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(e.FullName) && !e.FullName.Contains(':') && names.Add(e.FullName), "백업에 잘못된 경로 또는 중복 파일이 있습니다.");
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); e.ExtractToFile(target);
            }
            var manifestFile = Path.Combine(stage, "manifest.json"); Guard.Require(File.Exists(manifestFile), "백업 manifest가 없습니다."); var manifest = BusinessService.Deserialize<BackupManifest>(File.ReadAllText(manifestFile)); Guard.Require(manifest.SchemaVersion == 1, "호환되지 않는 백업 스키마입니다.");
            Guard.Require(manifest.Files.Count == manifest.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "백업 파일 목록이 중복되었습니다.");
            Guard.Require(manifest.Files.Any(x => x.Path == "imorder.db") && manifest.Files.Any(x => x.Path == "settings.json"), "필수 백업 파일이 없습니다.");
            var actual = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(stage, x).Replace('\\', '/')).Where(x => x != "manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
            Guard.Require(actual.SetEquals(manifest.Files.Select(x => x.Path)), "백업 파일 목록이 일치하지 않습니다.");
            foreach (var entry in manifest.Files) { var path = Path.GetFullPath(Path.Combine(stage, entry.Path)); Guard.Require(path.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path) && new FileInfo(path).Length == entry.Size && Hash(path) == entry.Sha256, "백업 해시 또는 파일 검증에 실패했습니다."); }
            using var connection = new SqliteConnection($"Data Source={Path.Combine(stage, "imorder.db")};Mode=ReadOnly;Pooling=False"); connection.Open(); using var check = connection.CreateCommand(); check.CommandText = "PRAGMA integrity_check"; Guard.Require((string?)check.ExecuteScalar() == "ok", "DB 무결성 검사에 실패했습니다."); check.CommandText = "PRAGMA foreign_key_check"; using (var reader = check.ExecuteReader()) Guard.Require(!reader.Read(), "DB 외래키 검사에 실패했습니다.");
            check.CommandText = "SELECT RelativePath, Sha256 FROM Attachments WHERE Status = 'Active'"; using (var reader = check.ExecuteReader()) while (reader.Read()) { var path = Path.GetFullPath(Path.Combine(stage, reader.GetString(0))); Guard.Require(path.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path) && Hash(path) == reader.GetString(1), "첨부 무결성 검사에 실패했습니다."); }
            _ = BusinessService.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(stage, "settings.json"))); return manifest;
        }
        finally { if (!keep && Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public async Task Restore(string name)
    {
        Guard.Require(Path.GetFileName(name) == name && IsBackupName(name), "백업 파일명을 확인해 주세요."); var zip = Path.Combine(paths.Backups, name); Guard.Require(File.Exists(zip), "백업이 없습니다.", "NOT_FOUND");
        await RestorePath(zip, name);
    }
    public async Task RestorePath(string zip, string name)
    {
        await gate.Semaphore.WaitAsync(); var stage = Path.Combine(paths.Temp, "restore-" + Guid.NewGuid().ToString("N")); var rollback = Path.Combine(paths.Temp, "rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            VerifyBackup(zip, stage, true); SchemaUpgrade.EnsureCustomerPriority(Path.Combine(stage, "imorder.db")); await db.Database.CloseConnectionAsync(); SqliteConnection.ClearAllPools(); Directory.CreateDirectory(rollback);
            var targets = new[] { "imorder.db", "Attachments", "settings.json" };
            foreach (var suffix in new[] { "-wal", "-shm" }) { var file = paths.Database + suffix; if (File.Exists(file)) File.Delete(file); }
            var movedOld = new List<string>(); var movedNew = new List<string>();
            try
            {
                foreach (var target in targets) { var current = paths.Resolve(target); var saved = Path.Combine(rollback, target); if (Directory.Exists(current)) { Directory.Move(current, saved); movedOld.Add(target); } else if (File.Exists(current)) { File.Move(current, saved); movedOld.Add(target); } }
                foreach (var target in targets) { var src = Path.Combine(stage, target); if (target == "Attachments" && !Directory.Exists(src)) Directory.CreateDirectory(src); if (Directory.Exists(src)) Directory.Move(src, paths.Resolve(target)); else File.Move(src, paths.Resolve(target)); movedNew.Add(target); }
                File.AppendAllText(paths.Resolve("operations.log"), $"{DateTime.UtcNow:O} Restore {name} completed\n"); db.ChangeTracker.Clear();
            }
            catch
            {
                foreach (var target in movedNew) { var p = paths.Resolve(target); if (Directory.Exists(p)) Directory.Move(p, Path.Combine(stage, target)); else if (File.Exists(p)) File.Move(p, Path.Combine(stage, target)); }
                foreach (var target in movedOld) { var p = Path.Combine(rollback, target); if (Directory.Exists(p)) Directory.Move(p, paths.Resolve(target)); else File.Move(p, paths.Resolve(target)); }
                File.AppendAllText(paths.Resolve("operations.log"), $"{DateTime.UtcNow:O} Restore failed; rolled back\n"); throw;
            }
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); if (Directory.Exists(rollback)) Directory.Delete(rollback, true); gate.Semaphore.Release(); }
    }
}
