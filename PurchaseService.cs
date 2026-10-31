using Microsoft.EntityFrameworkCore;

namespace ImOrder;

public class PurchaseOrder : Record
{
    public string Number { get; set; } = "";
    public long SupplierId { get; set; }
    public long? SalesOrderId { get; set; }
    public string Title { get; set; } = "";
    public DateOnly Date { get; set; }
    public DateOnly? DueDate { get; set; }
    public long SupplyAmount { get; set; }
    public long VatAmount { get; set; }
    public long TotalAmount { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string PolicyJson { get; set; } = "{}";
    public string SupplierSnapshotJson { get; set; } = "{}";
    public string Memo { get; set; } = "";
    public string Status { get; set; } = "Open";
}
public class PurchaseReceipt : Record
{
    public long PurchaseOrderId { get; set; }
    public DateOnly Date { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string Memo { get; set; } = "";
    public string Status { get; set; } = "Active";
}
public class PurchaseBill : Record
{
    public long PurchaseOrderId { get; set; }
    public DateOnly Date { get; set; }
    public DateOnly? DueDate { get; set; }
    public long SupplyAmount { get; set; }
    public long VatAmount { get; set; }
    public long TotalAmount { get; set; }
    public string Reference { get; set; } = "";
    public string Memo { get; set; } = "";
    public string Status { get; set; } = "Active";
}
public class PurchaseDisbursement : Record
{
    public long PurchaseOrderId { get; set; }
    public DateOnly Date { get; set; }
    public long Amount { get; set; }
    public string Reference { get; set; } = "";
    public string Memo { get; set; } = "";
    public string Status { get; set; } = "Active";
}
public record PurchaseOrderInput(long SupplierId, long? SalesOrderId, string Title, DateOnly Date, DateOnly? DueDate, List<ItemInput> Items, TaxPolicy Policy, string Memo, int ExpectedVersion, string IdempotencyKey);
public record PurchaseReceiptLine(int ItemIndex, decimal Quantity);
public record PurchaseReceiptInput(DateOnly Date, List<PurchaseReceiptLine> Items, string Memo, int ExpectedVersion, string IdempotencyKey);
public record PurchaseBillInput(DateOnly Date, DateOnly? DueDate, long SupplyAmount, long VatAmount, string Reference, string Memo, int ExpectedVersion, string IdempotencyKey);
public record PurchaseDisbursementInput(DateOnly Date, long Amount, string Reference, string Memo, int ExpectedVersion, string IdempotencyKey);
public record PurchaseTotals(long PurchaseOrderId, long Ordered, long Billed, long Paid, long Unbilled, long Payable, long Remaining);

public class PurchaseService(AppDb db, BusinessService business, ActorContext actor)
{
    static void Touch(Record r) { r.Version++; r.UpdatedAtUtc = DateTime.UtcNow; }
    void UserOnly() => Guard.Require(actor.Source == "UI", "사용자 화면에서만 처리할 수 있습니다.", "FORBIDDEN");
    async Task Key(string key, string operation)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(key) && key.Length <= 100, "요청 식별키를 확인해 주세요.");
        Guard.Require(!await db.Receipts.AnyAsync(x => x.Key == key), "이미 처리한 요청입니다. 새로고침해 주세요.", "DUPLICATE_REQUEST");
    }
    void Receipt(string key, string operation, long id) => db.Receipts.Add(new() { Key = key, Operation = operation, RecordId = id });
    async Task<PurchaseOrder> Open(long id, int version)
    {
        var po = Guard.Found(await db.PurchaseOrders.FindAsync(id)); Guard.Version(po, version);
        Guard.Require(po.Status == "Open", "취소된 발주는 변경할 수 없습니다."); return po;
    }
    public async Task<PurchaseTotals> Totals(long id)
    {
        var po = Guard.Found(await db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id));
        var billed = await db.PurchaseBills.Where(x => x.PurchaseOrderId == id && x.Status == "Active").SumAsync(x => x.TotalAmount);
        var paid = await db.PurchaseDisbursements.Where(x => x.PurchaseOrderId == id && x.Status == "Active").SumAsync(x => x.Amount);
        return po.Status == "Cancelled" ? new(id, po.TotalAmount, billed, paid, 0, 0, 0) : new(id, po.TotalAmount, billed, paid, po.TotalAmount - billed, Math.Max(0, billed - paid), po.TotalAmount - paid);
    }
    public Task<PurchaseOrder> SaveOrder(PurchaseOrderInput input, long? id = null) => business.Write(async () =>
    {
        UserOnly(); await Key(input.IdempotencyKey, "PurchaseOrderSave");
        var supplier = Guard.Found(await db.Customers.FindAsync(input.SupplierId)); Guard.Require(supplier.IsActive, "사용 중인 공급처를 선택해 주세요.");
        Guard.Require(!string.IsNullOrWhiteSpace(input.Title), "발주명을 입력해 주세요."); Guard.Require(input.Date != default, "발주일을 입력해 주세요.");
        Guard.Require(input.DueDate == null || input.DueDate >= input.Date, "납기는 발주일 이후여야 합니다.");
        if (input.SalesOrderId is long salesId) Guard.Found(await db.Orders.FindAsync(salesId));
        var amounts = MoneyCalculator.Calculate(input.Items, input.Policy); PurchaseOrder po; string? old = null;
        if (id is long existing)
        {
            po = await Open(existing, input.ExpectedVersion);
            Guard.Require(!await db.PurchaseReceipts.AnyAsync(x => x.PurchaseOrderId == existing && x.Status == "Active") && !await db.PurchaseBills.AnyAsync(x => x.PurchaseOrderId == existing && x.Status == "Active") && !await db.PurchaseDisbursements.AnyAsync(x => x.PurchaseOrderId == existing && x.Status == "Active"), "입고·매입·지급이 등록된 발주는 수정할 수 없습니다. 관련 내역을 먼저 취소해 주세요.");
            old = BusinessService.Serialize(po); Touch(po);
        }
        else { po = new() { Number = $"PO-{BusinessService.Today:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}" }; db.PurchaseOrders.Add(po); }
        po.SupplierId = supplier.Id; po.SalesOrderId = input.SalesOrderId; po.Title = Guard.Text(input.Title, 300); po.Date = input.Date; po.DueDate = input.DueDate;
        po.ItemsJson = BusinessService.Serialize(amounts.Items); po.PolicyJson = BusinessService.Serialize(input.Policy); po.SupplierSnapshotJson = BusinessService.Serialize(supplier);
        po.SupplyAmount = amounts.SupplyAmount; po.VatAmount = amounts.VatAmount; po.TotalAmount = amounts.TotalAmount; po.Memo = Guard.Text(input.Memo ?? "", 5000);
        await db.SaveChangesAsync(); business.Audit(po, id == null ? "Create" : "Update", old); Receipt(input.IdempotencyKey, "PurchaseOrderSave", po.Id); return po;
    });
    public Task<PurchaseReceipt> AddReceipt(long id, PurchaseReceiptInput input) => business.Write(async () =>
    {
        UserOnly(); await Key(input.IdempotencyKey, "PurchaseReceipt"); var po = await Open(id, input.ExpectedVersion);
        Guard.Require(input.Date != default, "입고일을 입력해 주세요."); var ordered = BusinessService.Deserialize<List<CalculatedItem>>(po.ItemsJson); var lines = input.Items ?? [];
        Guard.Require(lines.Count > 0 && lines.Select(x => x.ItemIndex).Distinct().Count() == lines.Count, "입고 수량을 입력해 주세요.");
        var existing = await db.PurchaseReceipts.Where(x => x.PurchaseOrderId == id && x.Status == "Active").ToListAsync();
        var prior = existing.SelectMany(x => BusinessService.Deserialize<List<PurchaseReceiptLine>>(x.ItemsJson)).GroupBy(x => x.ItemIndex).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        foreach (var line in lines) { Guard.Require(line.ItemIndex >= 0 && line.ItemIndex < ordered.Count && line.Quantity > 0 && decimal.Round(line.Quantity, 4) == line.Quantity, "입고 품목과 수량을 확인해 주세요."); Guard.Require(line.Quantity + prior.GetValueOrDefault(line.ItemIndex) <= ordered[line.ItemIndex].Input.Quantity, "입고 수량이 발주 수량을 초과합니다."); }
        var row = new PurchaseReceipt { PurchaseOrderId = id, Date = input.Date, ItemsJson = BusinessService.Serialize(lines), Memo = Guard.Text(input.Memo ?? "", 5000) }; db.PurchaseReceipts.Add(row); Touch(po); await db.SaveChangesAsync(); business.Audit(row, "Create"); Receipt(input.IdempotencyKey, "PurchaseReceipt", row.Id); return row;
    });
    public Task<PurchaseBill> AddBill(long id, PurchaseBillInput input) => business.Write(async () =>
    {
        UserOnly(); await Key(input.IdempotencyKey, "PurchaseBill"); var po = await Open(id, input.ExpectedVersion);
        Guard.Require(input.Date != default, "매입일을 입력해 주세요."); Guard.Require(input.DueDate == null || input.DueDate >= input.Date, "지급기한은 매입일 이후여야 합니다.");
        Guard.Require(input.SupplyAmount >= 0 && input.VatAmount >= 0 && input.SupplyAmount <= MoneyCalculator.MaxMoney && input.VatAmount <= MoneyCalculator.MaxMoney && input.SupplyAmount + input.VatAmount > 0, "매입금액을 확인해 주세요.");
        var totals = await Totals(id); Guard.Require(input.SupplyAmount + input.VatAmount <= totals.Unbilled, "매입 합계가 발주 금액을 초과합니다.");
        var row = new PurchaseBill { PurchaseOrderId = id, Date = input.Date, DueDate = input.DueDate, SupplyAmount = input.SupplyAmount, VatAmount = input.VatAmount, TotalAmount = input.SupplyAmount + input.VatAmount, Reference = Guard.Text(input.Reference ?? ""), Memo = Guard.Text(input.Memo ?? "", 5000) }; db.PurchaseBills.Add(row); Touch(po); await db.SaveChangesAsync(); business.Audit(row, "Create"); Receipt(input.IdempotencyKey, "PurchaseBill", row.Id); return row;
    });
    public Task<PurchaseDisbursement> AddPayment(long id, PurchaseDisbursementInput input) => business.Write(async () =>
    {
        UserOnly(); await Key(input.IdempotencyKey, "PurchasePayment"); var po = await Open(id, input.ExpectedVersion);
        Guard.Require(input.Date != default, "지급일을 입력해 주세요."); Guard.Require(input.Amount > 0 && input.Amount <= MoneyCalculator.MaxMoney, "지급 금액을 확인해 주세요."); var totals = await Totals(id); Guard.Require(input.Amount <= totals.Remaining, "지급액이 발주 잔액을 초과합니다.");
        var row = new PurchaseDisbursement { PurchaseOrderId = id, Date = input.Date, Amount = input.Amount, Reference = Guard.Text(input.Reference ?? ""), Memo = Guard.Text(input.Memo ?? "", 5000) }; db.PurchaseDisbursements.Add(row); Touch(po); await db.SaveChangesAsync(); business.Audit(row, "Create"); Receipt(input.IdempotencyKey, "PurchasePayment", row.Id); return row;
    });
    public Task<Record> Cancel(string kind, long id, CommandMeta meta) => business.Write<Record>(async () =>
    {
        UserOnly(); await Key(meta.IdempotencyKey, "PurchaseCancel"); Guard.Require(!string.IsNullOrWhiteSpace(meta.Reason), "취소 사유를 입력해 주세요."); Record row; PurchaseOrder po;
        if (kind == "order") { po = Guard.Found(await db.PurchaseOrders.FindAsync(id)); Guard.Version(po, meta.ExpectedVersion); Guard.Require(po.Status == "Open", "이미 취소된 발주입니다."); Guard.Require(!await db.PurchaseReceipts.AnyAsync(x => x.PurchaseOrderId == id && x.Status == "Active") && !await db.PurchaseBills.AnyAsync(x => x.PurchaseOrderId == id && x.Status == "Active") && !await db.PurchaseDisbursements.AnyAsync(x => x.PurchaseOrderId == id && x.Status == "Active"), "입고·매입·지급 내역을 먼저 취소해 주세요."); row = po; }
        else if (kind == "receipt") { var x = Guard.Found(await db.PurchaseReceipts.FindAsync(id)); Guard.Version(x, meta.ExpectedVersion); Guard.Require(x.Status == "Active", "이미 취소된 입고입니다."); po = Guard.Found(await db.PurchaseOrders.FindAsync(x.PurchaseOrderId)); row = x; }
        else if (kind == "bill") { var x = Guard.Found(await db.PurchaseBills.FindAsync(id)); Guard.Version(x, meta.ExpectedVersion); Guard.Require(x.Status == "Active", "이미 취소된 매입입니다."); po = Guard.Found(await db.PurchaseOrders.FindAsync(x.PurchaseOrderId)); row = x; }
        else if (kind == "payment") { var x = Guard.Found(await db.PurchaseDisbursements.FindAsync(id)); Guard.Version(x, meta.ExpectedVersion); Guard.Require(x.Status == "Active", "이미 취소된 지급입니다."); po = Guard.Found(await db.PurchaseOrders.FindAsync(x.PurchaseOrderId)); row = x; }
        else throw new BusinessException("VALIDATION_ERROR", "처리할 내역을 확인해 주세요.");
        var old = BusinessService.Serialize(row); if (row is PurchaseOrder order) order.Status = "Cancelled"; else if (row is PurchaseReceipt receipt) receipt.Status = "Cancelled"; else if (row is PurchaseBill bill) bill.Status = "Cancelled"; else if (row is PurchaseDisbursement payment) payment.Status = "Cancelled";
        Touch(row); if (row != po) Touch(po); business.Audit(row, "Cancel", old, Guard.Text(meta.Reason, 1000)); Receipt(meta.IdempotencyKey, "PurchaseCancel", id); return row;
    });
}
