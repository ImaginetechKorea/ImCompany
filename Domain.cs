using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace ImOrder;

public class Record
{
    public long Id { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
public class Customer : Record
{
    public string Code { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string BusinessNumber { get; set; } = "";
    public string CeoName { get; set; } = "";
    public string BusinessType { get; set; } = "";
    public string BusinessItem { get; set; } = "";
    public string Address { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Memo { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool IsPreferred { get; set; }
}
public class Document : Record
{
    public string Number { get; set; } = "";
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string ProjectName { get; set; } = "";
    public string SiteName { get; set; } = "";
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public DateOnly? DueDate { get; set; }
    public long SupplyAmount { get; set; }
    public long VatAmount { get; set; }
    public long TotalAmount { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public string ItemsJson { get; set; } = "[]";
    public string PolicyJson { get; set; } = "{}";
    public string Memo { get; set; } = "";
    public string Status { get; set; } = "Draft";
}
public class Quotation : Document { }
public class SalesOrder : Document
{
    public long? QuotationId { get; set; }
    public Quotation? Quotation { get; set; }
    public string CustomerPONo { get; set; } = "";
    public string PaymentTermsJson { get; set; } = "{}";
}
public class Invoice : Document
{
    public long OrderId { get; set; }
    public SalesOrder? Order { get; set; }
    public string InvoiceType { get; set; } = "General";
    public bool TaxInvoiceIssued { get; set; }
    public DateOnly? TaxInvoiceDate { get; set; }
    public string TaxApprovalNo { get; set; } = "";
    public DateTime? ConfirmedAtUtc { get; set; }
}
public class Fulfillment : Record
{
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public string? IdempotencyKey { get; set; }
    public long OrderId { get; set; }
    public SalesOrder? Order { get; set; }
    public string Type { get; set; } = "Delivery";
    public DateOnly? PlannedDate { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public string Description { get; set; } = "";
    public decimal? Quantity { get; set; }
    public string Status { get; set; } = "Planned";
    public string Memo { get; set; } = "";
}
public class Payment : Record
{
    public long? OrderId { get; set; }
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateOnly Date { get; set; }
    public long Amount { get; set; }
    public string BankName { get; set; } = "";
    public string AccountMasked { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public string Memo { get; set; } = "";
}
public class Allocation : Record
{
    public long PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public long InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public long Amount { get; set; }
    public string Status { get; set; } = "Active";
    public string IdempotencyKey { get; set; } = "";
    public DateTime? ReversedAtUtc { get; set; }
}
public class Attachment : Record
{
    public string EntityType { get; set; } = "";
    public long EntityId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "Pending";
}
public class AuditLog
{
    public long Id { get; set; }
    public string EntityType { get; set; } = "";
    public long RecordId { get; set; }
    public string Action { get; set; } = "";
    public string OldValueJson { get; set; } = "null";
    public string NewValueJson { get; set; } = "null";
    public string ActorId { get; set; } = "local-owner";
    public string ActorType { get; set; } = "User";
    public string Source { get; set; } = "UI";
    public string CorrelationId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
public class RequestReceipt
{
    [Key] public string Key { get; set; } = "";
    public string Operation { get; set; } = "";
    public long RecordId { get; set; }
}
public class Asset : Record
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public DateOnly? PurchaseDate { get; set; }
    public long PurchaseAmount { get; set; }
    public DateOnly? DepreciationStartDate { get; set; }
    public int? UsefulLifeYears { get; set; }
    public long ResidualValue { get; set; }
    public string DepreciationMethod { get; set; } = "None";
    public long? ImmediateExpenseAmount { get; set; }
    public int? DepreciationRateBps { get; set; }
    public string Vendor { get; set; } = "";
    public string EvidenceType { get; set; } = "";
    public long? TotalPaid { get; set; }
    public long? SupplyValue { get; set; }
    public long? VatValue { get; set; }
    public bool? InputVatDeductible { get; set; }
    public int? BusinessUseBps { get; set; }
    public string Location { get; set; } = "";
    public string Custodian { get; set; } = "";
    public string Status { get; set; } = "InUse";
    public long? OrderId { get; set; }
    public string Memo { get; set; } = "";
}
public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Quotation> Quotations => Set<Quotation>();
    public DbSet<SalesOrder> Orders => Set<SalesOrder>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Allocation> Allocations => Set<Allocation>();
    public DbSet<Fulfillment> Fulfillments => Set<Fulfillment>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RequestReceipt> Receipts => Set<RequestReceipt>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseReceipt> PurchaseReceipts => Set<PurchaseReceipt>();
    public DbSet<PurchaseBill> PurchaseBills => Set<PurchaseBill>();
    public DbSet<PurchaseDisbursement> PurchaseDisbursements => Set<PurchaseDisbursement>();
    protected override void OnModelCreating(ModelBuilder m)
    {
        // Separate tables keep document ownership and foreign keys explicit.
        m.Ignore<Document>();
        m.Ignore<Record>();
        foreach (var t in new[] { typeof(Customer), typeof(Quotation), typeof(SalesOrder), typeof(Invoice), typeof(Payment), typeof(Allocation), typeof(Fulfillment), typeof(Attachment), typeof(PurchaseOrder), typeof(PurchaseReceipt), typeof(PurchaseBill), typeof(PurchaseDisbursement), typeof(Asset) })
        {
            m.Entity(t).HasKey("Id");
            m.Entity(t).Property("Version").IsConcurrencyToken();
        }
        m.Entity<PurchaseOrder>().HasOne<Customer>().WithMany().HasForeignKey(x => x.SupplierId);
        m.Entity<PurchaseOrder>().HasOne<SalesOrder>().WithMany().HasForeignKey(x => x.SalesOrderId);
        m.Entity<PurchaseReceipt>().HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId);
        m.Entity<PurchaseBill>().HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId);
        m.Entity<PurchaseDisbursement>().HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId);
        m.Entity<PurchaseOrder>().HasIndex(x => x.Number).IsUnique();
        m.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        m.Entity<Quotation>().HasIndex(x => x.Number).IsUnique();
        m.Entity<SalesOrder>().HasIndex(x => x.Number).IsUnique();
        m.Entity<SalesOrder>().HasIndex(x => x.QuotationId).IsUnique();
        m.Entity<Invoice>().HasIndex(x => x.Number).IsUnique();
        m.Entity<Allocation>().HasIndex(x => x.IdempotencyKey).IsUnique();
        m.Entity<Invoice>().HasIndex(x => new { x.OrderId, x.Status });
        m.Entity<Invoice>().HasIndex(x => new { x.DueDate, x.Status });
        m.Entity<Allocation>().HasIndex(x => new { x.PaymentId, x.Status });
        m.Entity<Allocation>().HasIndex(x => new { x.InvoiceId, x.Status });
        m.Entity<Attachment>().HasIndex(x => new { x.EntityType, x.EntityId });
        m.Entity<AuditLog>().HasIndex(x => new { x.EntityType, x.RecordId, x.OccurredAtUtc });
        foreach (var fk in m.Model.GetEntityTypes().SelectMany(x => x.GetForeignKeys())) fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}

public record TaxPolicy(string TaxMode = "Exclusive", string RoundingMode = "Floor", string VatCalculationUnit = "Document", int CalculationPolicyVersion = 1);
public record ItemInput(string Description, decimal Quantity, decimal UnitPrice, decimal DiscountAmount = 0, string Unit = "식", string Specification = "", string TaxCategory = "Taxable", decimal VatRate = 0.1m);
public record CalculatedItem(ItemInput Input, long SupplyAmount, long VatAmount, long TotalAmount);
public record CalculatedDocument(List<CalculatedItem> Items, long SupplyAmount, long VatAmount, long TotalAmount);
public class BusinessException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public static class Guard
{
    public static void Require([DoesNotReturnIf(false)] bool condition, string message, string code = "VALIDATION_ERROR") { if (!condition) throw new BusinessException(code, message); }
    public static T Found<T>(T? value) where T : class => value ?? throw new BusinessException("NOT_FOUND", "자료를 찾을 수 없습니다.");
    public static void Version(Record row, int expected) => Require(row.Version == expected, "다른 작업에서 변경되었습니다. 새로고침 후 다시 시도해 주세요.", "VERSION_CONFLICT");
    public static string Text(string value, int max = 500) { Require(value.Length <= max, $"입력은 {max}자 이하여야 합니다."); return value.Trim(); }
}
public static class MoneyCalculator
{
    public const long MaxMoney = 9_000_000_000_000;
    public static long Round(decimal value, TaxPolicy p) => checked((long)(p.RoundingMode == "Round" ? decimal.Round(value, 0, MidpointRounding.AwayFromZero) : decimal.Floor(value)));
    public static CalculatedDocument Calculate(List<ItemInput> items, TaxPolicy p)
    {
        Guard.Require(p.TaxMode is "Inclusive" or "Exclusive" && p.RoundingMode is "Floor" or "Round" && p.VatCalculationUnit is "Item" or "Document", "세금 계산 정책이 올바르지 않습니다.");
        Guard.Require(items.Count is > 0 and <= 500, "품목은 1~500개 입력해 주세요.");
        var rows = new List<CalculatedItem>();
        foreach (var i in items)
        {
            Guard.Require(!string.IsNullOrWhiteSpace(i.Description) && i.Description.Length <= 1000, "품목명을 입력해 주세요.");
            Guard.Require(i.Quantity > 0 && i.Quantity <= 100_000_000 && i.UnitPrice >= 0 && i.UnitPrice <= MaxMoney && i.DiscountAmount >= 0, "수량·단가·할인액 범위를 확인해 주세요.");
            Guard.Require(decimal.Round(i.Quantity, 4) == i.Quantity && decimal.Round(i.UnitPrice, 4) == i.UnitPrice && decimal.Round(i.DiscountAmount, 4) == i.DiscountAmount, "수량, 단가, 할인은 소수점 4자리까지 지원합니다.");
            Guard.Require(i.TaxCategory is "Taxable" or "Exempt" or "ZeroRated" && i.VatRate is >= 0 and <= 1, "세금 구분과 세율을 확인해 주세요.");
            var raw = i.Quantity * i.UnitPrice - i.DiscountAmount;
            Guard.Require(raw is >= 0 and <= MaxMoney, "할인 후 금액 범위를 확인해 주세요.");
            var amount = Round(raw, p); var rate = i.TaxCategory == "Taxable" ? i.VatRate : 0;
            long supply = p.TaxMode == "Inclusive" ? Round(amount / (1 + rate), p) : amount;
            long vat = p.TaxMode == "Inclusive" ? amount - supply : Round(supply * rate, p);
            rows.Add(new(i, supply, vat, supply + vat));
        }
        if (p.VatCalculationUnit == "Document")
        {
            foreach (var group in rows.Select((row, index) => (row, index)).GroupBy(x => (x.row.Input.TaxCategory, Rate: x.row.Input.TaxCategory == "Taxable" ? x.row.Input.VatRate : 0)))
            {
                var list = group.ToList(); var last = list[^1].index;
                var sumSupply = list.Sum(x => x.row.SupplyAmount); var sumTotal = list.Sum(x => x.row.TotalAmount);
                var targetVat = p.TaxMode == "Inclusive" ? sumTotal - Round(sumTotal / (1 + group.Key.Rate), p) : Round(sumSupply * group.Key.Rate, p);
                var delta = targetVat - list.Sum(x => x.row.VatAmount);
                // Prefer the last line; spread larger adjustments backwards to avoid negative line amounts.
                foreach (var entry in list.AsEnumerable().Reverse())
                {
                    if (delta == 0) break;
                    var row = rows[entry.index];
                    var change = delta > 0 ? (p.TaxMode == "Inclusive" ? Math.Min(delta, row.SupplyAmount) : delta) : -Math.Min(-delta, row.VatAmount);
                    rows[entry.index] = p.TaxMode == "Inclusive" ? row with { SupplyAmount = row.SupplyAmount - change, VatAmount = row.VatAmount + change } : row with { VatAmount = row.VatAmount + change, TotalAmount = row.TotalAmount + change };
                    delta -= change;
                }
                Guard.Require(delta == 0, "품목별 세액 조정이 불가능합니다.");
            }
        }
        Guard.Require(rows.Sum(x => x.TotalAmount) <= MaxMoney, "문서 금액이 허용 범위를 초과했습니다.");
        return new(rows, rows.Sum(x => x.SupplyAmount), rows.Sum(x => x.VatAmount), rows.Sum(x => x.TotalAmount));
    }
}
