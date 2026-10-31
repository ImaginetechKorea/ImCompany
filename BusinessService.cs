using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ImOrder;

public record CommandMeta(int ExpectedVersion, string IdempotencyKey, string Reason = "");
public record DocumentInput(long CustomerId, string ProjectName, DateOnly Date, DateOnly? DueDate, List<ItemInput> Items, TaxPolicy Policy, string SiteName = "", string Memo = "", int ExpectedVersion = 0, string? IdempotencyKey = null);
public record TaxDocumentInput(bool ConfirmedSales, long? ExpectedVatAmount, long? ExpectedTotalAmount, string SupplierBusinessNumber = "", string RecipientBusinessNumber = "", bool Issued = false, DateOnly? TaxInvoiceDate = null, string TaxApprovalNo = "");
public record InvoiceInput(long OrderId, string InvoiceType, DateOnly Date, DateOnly? DueDate, long? Amount, decimal? Percentage, int ExpectedVersion, string IdempotencyKey, TaxDocumentInput? TaxDocument = null);
public record InvoiceUpdateInput(string InvoiceType, DateOnly Date, DateOnly? DueDate, long SupplyAmount, int ExpectedVersion, string IdempotencyKey, string Reason);
public record PaymentInput(long CustomerId, DateOnly Date, long Amount, string BankName = "", string Reference = "", string Memo = "", string? IdempotencyKey = null, long? OrderId = null);
public record PaymentUpdateInput(long? OrderId, DateOnly Date, long Amount, string BankName, string Reference, string Memo, int ExpectedVersion, string IdempotencyKey);
public record PaymentProjectInput(long OrderId, int ExpectedVersion, string IdempotencyKey);
public record AllocationInput(long PaymentId, long InvoiceId, long Amount, int ExpectedVersion, int InvoiceVersion, string IdempotencyKey);
public record OrderDetailsInput(DateOnly? DeliveryDate, string CustomerPONo, string PaymentTermsJson, string Memo, int ExpectedVersion, string? ProjectName = null);
public record ContractChangeInput(List<ItemInput> Items, TaxPolicy Policy, int ExpectedVersion, string Reason, string IdempotencyKey);
public record TaxInvoiceInput(bool Issued, DateOnly? Date, string ApprovalNo, int ExpectedVersion);
public record FinancialStatus(long OrderId, string Currency, string AmountBasis, long OrderTotal, long Invoiced, long Paid, long Outstanding, long Uninvoiced, long TotalRemaining, int RecordVersion, long[] SourceInvoiceIds);
public class WriteGate { public SemaphoreSlim Semaphore { get; } = new(1, 1); }
public class ActorContext
{
    public string Source { get; set; } = "UI";
    public string ActorType => Source == "UI" ? "User" : "AI";
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
}
public class BusinessService(AppDb db, WriteGate gate, ActorContext actor, SettingsStore settings)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Json);
    public static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;
    public AppDb Db => db;
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Korea Standard Time"));
    public async Task<T> Write<T>(Func<Task<T>> action)
    {
        await gate.Semaphore.WaitAsync();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            try { var result = await action(); await db.SaveChangesAsync(); await tx.CommitAsync(); return result; }
            catch { await tx.RollbackAsync(); db.ChangeTracker.Clear(); throw; }
        }
        catch (DbUpdateConcurrencyException) { throw new BusinessException("VERSION_CONFLICT", "자료가 변경되었습니다. 새로고침해 주세요."); }
        finally { gate.Semaphore.Release(); }
    }
    public void Audit(Record row, string action, object? old = null, string reason = "")
    {
        db.AuditLogs.Add(new() { EntityType = row.GetType().Name, RecordId = row.Id, Action = action, OldValueJson = old is string s ? s : Serialize(old), NewValueJson = Serialize(row), Source = actor.Source, ActorType = actor.ActorType, CorrelationId = actor.CorrelationId, Reason = reason });
    }
    static void Touch(Record row) { row.Version++; row.UpdatedAtUtc = DateTime.UtcNow; }
    async Task CheckKey(string key, string op)
    {
        Guard.Require(key.Length is >= 8 and <= 100, "요청 식별키가 필요합니다.");
        Guard.Require(!await db.Receipts.AnyAsync(x => x.Key == key), "이미 처리한 요청입니다. 결과를 새로고침해 주세요.", "DUPLICATE_REQUEST");
    }
    void Receipt(string key, string operation, long id) => db.Receipts.Add(new() { Key = key, Operation = operation, RecordId = id });
    void UserOnly() => Guard.Require(actor.Source == "UI", "이 작업은 사용자 화면에서 승인해야 합니다.", "FORBIDDEN");
    public Task<Customer> SaveCustomer(Customer input, long? id = null) => Write(() => SaveCustomerCore(input, id));
    public Task<object> DeleteCustomer(long id, CommandMeta meta) => Write<object>(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "DeleteCustomer");
        var customer = Guard.Found(await db.Customers.FindAsync(id)); Guard.Version(customer, meta.ExpectedVersion);
        Guard.Require(!await db.Quotations.AnyAsync(x => x.CustomerId == id) && !await db.Orders.AnyAsync(x => x.CustomerId == id) && !await db.Invoices.AnyAsync(x => x.CustomerId == id) && !await db.Payments.AnyAsync(x => x.CustomerId == id) && !await db.PurchaseOrders.AnyAsync(x => x.SupplierId == id), "견적, 수주, 청구 또는 입금 내역이 있는 거래처는 삭제할 수 없습니다. 사용 안 함으로 변경해 주세요.");
        var old = Serialize(customer);
        foreach (var attachment in await db.Attachments.Where(x => x.EntityType == "Customer" && x.EntityId == id && x.Status == "Active").ToListAsync())
        { var previous = Serialize(attachment); attachment.Status = "Deleted"; Touch(attachment); Audit(attachment, "Delete", previous, "거래처 삭제"); }
        Audit(customer, "Delete", old, meta.Reason); db.Customers.Remove(customer); Receipt(meta.IdempotencyKey, "DeleteCustomer", id);
        return new { deleted = true, id };
    });
    internal async Task<Customer> SaveCustomerCore(Customer input, long? id = null)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(input.CompanyName), "거래처 상호를 입력해 주세요.");
        input.CompanyName = Guard.Text(input.CompanyName, 200);
        input.BusinessNumber = input.BusinessNumber.Replace("-", "").Trim();
        Guard.Require(input.BusinessNumber == "" || input.BusinessNumber.Length == 10 && input.BusinessNumber.All(char.IsAsciiDigit), "사업자번호는 숫자 10자리입니다.");
        Guard.Require(input.BusinessNumber == "" || !await db.Customers.AnyAsync(x => x.BusinessNumber == input.BusinessNumber && x.Id != (id ?? 0)), "동일 사업자번호의 거래처가 있습니다.");
        Guard.Require(input.Email.Length <= 254 && (input.Email == "" || System.Net.Mail.MailAddress.TryCreate(input.Email, out _)), "이메일 형식을 확인해 주세요.");
        Customer row; string? old = null;
        if (id is not null) { row = Guard.Found(await db.Customers.FindAsync(id)); Guard.Version(row, input.Version); old = Serialize(row); Touch(row); }
        else { row = new() { Code = "C-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant() }; db.Customers.Add(row); }
        row.CompanyName = input.CompanyName; row.BusinessNumber = input.BusinessNumber; row.CeoName = Guard.Text(input.CeoName); row.BusinessType = Guard.Text(input.BusinessType); row.BusinessItem = Guard.Text(input.BusinessItem); row.Address = Guard.Text(input.Address); row.ContactName = Guard.Text(input.ContactName); row.Phone = Guard.Text(input.Phone); row.Email = input.Email; row.Memo = Guard.Text(input.Memo, 5000); row.IsActive = input.IsActive; row.IsPreferred = input.IsPreferred;
        await db.SaveChangesAsync(); Audit(row, id is null ? "Create" : "Update", old); return row;
    }
    public Task<Quotation> SaveQuotation(DocumentInput input, long? id = null) => Write(async () =>
    {
        if (actor.Source != "UI") Guard.Require(input.IdempotencyKey != null, "AI 초안 생성에는 중복 방지 키가 필요합니다.");
        if (input.IdempotencyKey != null) await CheckKey(input.IdempotencyKey, "CreateQuotationDraft");
        var customer = Guard.Found(await db.Customers.FindAsync(input.CustomerId)); Guard.Require(customer.IsActive, "사용 중인 거래처를 선택해 주세요.");
        Guard.Require(!string.IsNullOrWhiteSpace(input.ProjectName), "프로젝트명을 입력해 주세요.");
        Guard.Require(input.DueDate == null || input.DueDate >= input.Date, "유효기간은 작성일 이후여야 합니다.");
        var money = MoneyCalculator.Calculate(input.Items, input.Policy); Quotation row; string? old = null;
        if (id is not null) { row = Guard.Found(await db.Quotations.FindAsync(id)); Guard.Version(row, input.ExpectedVersion); Guard.Require(row.Status == "Draft", "초안만 수정할 수 있습니다."); old = Serialize(row); Touch(row); }
        else { row = new() { Number = Number("QT") }; db.Quotations.Add(row); }
        row.CustomerId = customer.Id; row.ProjectName = Guard.Text(input.ProjectName, 300); row.SiteName = Guard.Text(input.SiteName); row.Date = input.Date; row.DueDate = input.DueDate; row.Memo = Guard.Text(input.Memo, 5000);
        row.SupplyAmount = money.SupplyAmount; row.VatAmount = money.VatAmount; row.TotalAmount = money.TotalAmount; row.ItemsJson = Serialize(money.Items); row.PolicyJson = Serialize(input.Policy); row.SnapshotJson = Serialize(customer);
        await db.SaveChangesAsync(); Audit(row, id is null ? "Create" : "Update", old); if (input.IdempotencyKey != null) Receipt(input.IdempotencyKey, "CreateQuotationDraft", row.Id); return row;
    });
    public Task<Quotation> ApproveQuotation(long id, CommandMeta meta) => Write(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "ApproveQuotation"); var q = Guard.Found(await db.Quotations.FindAsync(id)); Guard.Version(q, meta.ExpectedVersion); Guard.Require(q.Status == "Draft", "초안 견적만 승인할 수 있습니다.");
        var old = Serialize(q); q.SnapshotJson = Serialize(Guard.Found(await db.Customers.FindAsync(q.CustomerId))); q.Status = "Approved"; Touch(q); Audit(q, "Approve", old); Receipt(meta.IdempotencyKey, "ApproveQuotation", id); return q;
    });
    public Task<SalesOrder> ConvertQuotation(long id, CommandMeta meta) => Write(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "ConvertQuotation"); var q = Guard.Found(await db.Quotations.FindAsync(id)); Guard.Version(q, meta.ExpectedVersion); Guard.Require(q.Status == "Approved", "승인된 견적만 수주로 전환할 수 있습니다."); Guard.Require(!await db.Orders.AnyAsync(x => x.QuotationId == id), "이미 수주로 전환된 견적입니다.", "DUPLICATE_REQUEST");
        var o = new SalesOrder { Number = Number("SO"), CustomerId = q.CustomerId, QuotationId = id, ProjectName = q.ProjectName, SiteName = q.SiteName, Date = Today, SupplyAmount = q.SupplyAmount, VatAmount = q.VatAmount, TotalAmount = q.TotalAmount, ItemsJson = q.ItemsJson, PolicyJson = q.PolicyJson, SnapshotJson = q.SnapshotJson, Status = "Open", Memo = q.Memo };
        db.Orders.Add(o); await db.SaveChangesAsync(); Audit(o, "Create"); var old = Serialize(q); Touch(q); Audit(q, "Convert", old); Receipt(meta.IdempotencyKey, "ConvertQuotation", o.Id); return o;
    });
    public Task<SalesOrder> DirectOrder(DocumentInput input) => Write(async () =>
    {
        UserOnly();
        if (input.IdempotencyKey != null) { var receipt = await db.Receipts.FindAsync(input.IdempotencyKey); if (receipt != null) { Guard.Require(receipt.Operation == "CreateOrder", "이미 처리한 요청입니다.", "DUPLICATE_REQUEST"); return Guard.Found(await db.Orders.FindAsync(receipt.RecordId)); } await CheckKey(input.IdempotencyKey, "CreateOrder"); }
        var c = Guard.Found(await db.Customers.FindAsync(input.CustomerId)); Guard.Require(c.IsActive && !string.IsNullOrWhiteSpace(input.ProjectName), "거래처와 프로젝트명을 확인해 주세요."); Guard.Require(input.DueDate == null || input.DueDate >= input.Date, "납기는 수주일 이후여야 합니다.");
        var m = MoneyCalculator.Calculate(input.Items, input.Policy);
        var o = new SalesOrder { Number = Number("SO"), CustomerId = c.Id, ProjectName = Guard.Text(input.ProjectName, 300), Date = input.Date, DueDate = input.DueDate, SiteName = Guard.Text(input.SiteName), Memo = Guard.Text(input.Memo, 5000), SupplyAmount = m.SupplyAmount, VatAmount = m.VatAmount, TotalAmount = m.TotalAmount, ItemsJson = Serialize(m.Items), PolicyJson = Serialize(input.Policy), SnapshotJson = Serialize(c), Status = "Open" };
        db.Orders.Add(o); await db.SaveChangesAsync(); Audit(o, "Create"); if (input.IdempotencyKey != null) Receipt(input.IdempotencyKey, "CreateOrder", o.Id); return o;
    });
    public async Task<FinancialStatus> Financial(long id)
    {
        var o = Guard.Found(await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id));
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.OrderId == id && x.Status == "Confirmed").ToListAsync(); var ids = invoices.Select(x => x.Id).ToArray();
        var paid = await db.Allocations.Where(x => ids.Contains(x.InvoiceId) && x.Status == "Active" && x.Payment!.Status == "Confirmed").SumAsync(x => x.Amount);
        var direct = await db.Payments.Where(x => x.OrderId == id && x.Status == "Confirmed").SumAsync(x => x.Amount); paid += direct;
        var invoiced = invoices.Sum(x => x.TotalAmount);
        return new(id, "KRW", "vatIncluded", o.TotalAmount, invoiced, paid, Math.Max(0, invoiced - paid), o.TotalAmount - invoiced, o.TotalAmount - paid, o.Version, ids);
    }
    async Task AutoCloseWhenSettled(SalesOrder order)
    {
        if (order.Status != "Open") return;
        await db.SaveChangesAsync();
        var f = await Financial(order.Id);
        if (f.OrderTotal > 0 && f.TotalRemaining == 0 && f.Uninvoiced == 0)
        {
            var old = Serialize(order); order.Status = "Closed"; Touch(order);
            Audit(order, "AutoClose", old, "전체 입금과 청구 완료로 자동 종료");
        }
    }
    public Task<Invoice> CreateInvoice(InvoiceInput input) => Write(async () =>
    {
        await CheckKey(input.IdempotencyKey, "CreateInvoiceDraft"); var o = Guard.Found(await db.Orders.FindAsync(input.OrderId)); Guard.Version(o, input.ExpectedVersion); Guard.Require(o.Status == "Open", "진행 중인 수주만 청구할 수 있습니다.");
        DateOnly? dueDate = input.DueDate;
        Guard.Require(dueDate == null || dueDate >= input.Date, "지급기한은 청구일 이후여야 합니다.");
        Guard.Require(input.InvoiceType is "Deposit" or "Interim" or "Final" or "General" or "Additional", "청구유형을 확인해 주세요.");
        var confirmed = await db.Invoices.Where(x => x.OrderId == o.Id && x.Status == "Confirmed").ToListAsync(); var policy = Deserialize<TaxPolicy>(o.PolicyJson);
        long supply, vat;
        if (input.InvoiceType == "Final") { Guard.Require(input.Amount == null && input.Percentage == null, "최종 청구는 잔액으로 자동 계산합니다."); supply = o.SupplyAmount - confirmed.Sum(x => x.SupplyAmount); vat = o.VatAmount - confirmed.Sum(x => x.VatAmount); }
        else
        {
            Guard.Require((input.Amount is null) != (input.Percentage is null), "공급가액 또는 비율 중 하나를 입력해 주세요.");
            Guard.Require(input.Percentage == null || input.Percentage > 0 && input.Percentage <= 100, "비율은 0 초과 100 이하여야 합니다.");
            supply = input.Amount ?? MoneyCalculator.Round(o.SupplyAmount * input.Percentage!.Value / 100, policy);
            Guard.Require(supply is > 0 and <= MoneyCalculator.MaxMoney, "청구 공급가액을 확인해 주세요.");
            var groups = Deserialize<List<CalculatedItem>>(o.ItemsJson).GroupBy(x => new { x.Input.TaxCategory, Rate = x.Input.TaxCategory == "Taxable" ? x.Input.VatRate : 0 }).ToList();
            long assignedSupply = 0; vat = 0;
            for (int index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var partSupply = index == groups.Count - 1 ? supply - assignedSupply : MoneyCalculator.Round((decimal)supply * group.Sum(x => x.SupplyAmount) / o.SupplyAmount, policy);
                assignedSupply += partSupply; vat += MoneyCalculator.Round(partSupply * group.Key.Rate, policy);
            }
        }
        Guard.Require(supply >= 0 && vat >= 0 && supply + vat > 0, "남은 청구금액이 없습니다.");
        if (input.TaxDocument is { } tax)
        {
            UserOnly(); Guard.Require(tax.ConfirmedSales, "매출 세금계산서인지 확인해 주세요.");
            Guard.Require(tax.ExpectedVatAmount == null || tax.ExpectedVatAmount == vat, "세금계산서의 세액과 수주 기준으로 계산한 세액이 다릅니다. 수주와 세금 조건을 확인해 주세요.");
            Guard.Require(tax.ExpectedTotalAmount == null || tax.ExpectedTotalAmount == supply + vat, "세금계산서의 합계와 계산한 청구 합계가 다릅니다. 금액을 확인해 주세요.");
            static string Digits(string value) => value.Replace("-", "").Replace(" ", "").Trim();
            var supplier = Digits(tax.SupplierBusinessNumber ?? ""); var recipient = Digits(tax.RecipientBusinessNumber ?? "");
            Guard.Require((supplier == "" || supplier.Length == 10 && supplier.All(char.IsAsciiDigit)) && (recipient == "" || recipient.Length == 10 && recipient.All(char.IsAsciiDigit)), "세금계산서 사업자번호는 숫자 10자리로 확인해 주세요.");
            var own = Digits(settings.Read().BusinessNumber ?? ""); var customer = Guard.Found(await db.Customers.FindAsync(o.CustomerId)); var customerNumber = Digits(customer.BusinessNumber);
            Guard.Require(supplier == "" || own.Length != 10 || !own.All(char.IsAsciiDigit) || supplier == own, "세금계산서 공급자가 설정된 우리 회사 사업자번호와 다릅니다. 매입 문서인지 확인해 주세요.");
            Guard.Require(recipient == "" || customerNumber == "" || recipient == customerNumber, "세금계산서 공급받는 자와 선택한 수주의 거래처 사업자번호가 다릅니다.");
            Guard.Require(!tax.Issued || tax.TaxInvoiceDate != null && !string.IsNullOrWhiteSpace(tax.TaxApprovalNo), "발급 기록을 남기려면 작성일과 승인번호를 확인해 주세요.");
            if (!string.IsNullOrWhiteSpace(tax.TaxApprovalNo)) { var approval = Guard.Text(tax.TaxApprovalNo, 100); Guard.Require(!await db.Invoices.AnyAsync(x => x.TaxApprovalNo == approval && x.Status != "Cancelled" && x.Status != "Deleted"), "이미 등록된 세금계산서 승인번호입니다.", "DUPLICATE_REQUEST"); }
        }
        var confirmOnSave = actor.Source == "UI";
        if (confirmOnSave)
            Guard.Require(confirmed.Sum(x => x.SupplyAmount) + supply <= o.SupplyAmount && confirmed.Sum(x => x.VatAmount) + vat <= o.VatAmount && confirmed.Sum(x => x.TotalAmount) + supply + vat <= o.TotalAmount, "계약금액을 초과하는 청구입니다.", "OVER_INVOICE");
        var row = new Invoice { Number = Number("IV"), CustomerId = o.CustomerId, OrderId = o.Id, ProjectName = o.ProjectName, SiteName = o.SiteName, Date = input.Date, DueDate = dueDate, InvoiceType = input.InvoiceType, SupplyAmount = supply, VatAmount = vat, TotalAmount = supply + vat, PolicyJson = o.PolicyJson, SnapshotJson = o.SnapshotJson, ItemsJson = "[]", Status = confirmOnSave ? "Confirmed" : "Draft", ConfirmedAtUtc = confirmOnSave ? DateTime.UtcNow : null };
        if (confirmOnSave) Touch(o);
        if (input.TaxDocument is { } recorded) { row.TaxInvoiceIssued = recorded.Issued; row.TaxInvoiceDate = recorded.TaxInvoiceDate; row.TaxApprovalNo = Guard.Text(recorded.TaxApprovalNo ?? "", 100); }
        db.Invoices.Add(row); await db.SaveChangesAsync(); Audit(row, "Create"); Receipt(input.IdempotencyKey, "CreateInvoiceDraft", row.Id); if (confirmOnSave) await AutoCloseWhenSettled(o); return row;
    });
    public Task<Invoice> UpdateInvoice(long id, InvoiceUpdateInput input) => Write(async () =>
    {
        UserOnly(); await CheckKey(input.IdempotencyKey, "UpdateInvoice");
        var invoice = Guard.Found(await db.Invoices.FindAsync(id)); Guard.Version(invoice, input.ExpectedVersion);
        Guard.Require(invoice.Status is "Draft" or "Confirmed", "취소되거나 삭제된 청구는 수정할 수 없습니다.");
        var order = Guard.Found(await db.Orders.FindAsync(invoice.OrderId)); Guard.Require(order.Status == "Open", "종료된 수주를 먼저 재개해 주세요.");
        Guard.Require(input.InvoiceType is "Deposit" or "Interim" or "Final" or "General" or "Additional", "청구 유형을 확인해 주세요.");
        Guard.Require(input.DueDate == null || input.DueDate >= input.Date, "지급기한은 청구일 이후여야 합니다.");
        Guard.Require(!string.IsNullOrWhiteSpace(input.Reason), "수정 사유를 입력해 주세요.");
        Guard.Require(input.SupplyAmount is > 0 and <= MoneyCalculator.MaxMoney, "청구 공급가액을 확인해 주세요.");
        var policy = Deserialize<TaxPolicy>(order.PolicyJson); var others = await db.Invoices.Where(x => x.OrderId == order.Id && x.Status == "Confirmed" && x.Id != id).ToListAsync();
        long vat;
        if (input.SupplyAmount == invoice.SupplyAmount && (input.InvoiceType != "Final" || invoice.InvoiceType == "Final")) vat = invoice.VatAmount;
        else if (input.InvoiceType == "Final") { Guard.Require(input.SupplyAmount == order.SupplyAmount - others.Sum(x => x.SupplyAmount), "잔금 청구는 남은 공급가액으로 입력해 주세요."); vat = order.VatAmount - others.Sum(x => x.VatAmount); }
        else { var groups = Deserialize<List<CalculatedItem>>(order.ItemsJson).GroupBy(x => new { x.Input.TaxCategory, Rate = x.Input.TaxCategory == "Taxable" ? x.Input.VatRate : 0 }).ToList(); long assigned = 0; vat = 0; for (int n = 0; n < groups.Count; n++) { var g = groups[n]; var part = n == groups.Count - 1 ? input.SupplyAmount - assigned : MoneyCalculator.Round((decimal)input.SupplyAmount * g.Sum(x => x.SupplyAmount) / order.SupplyAmount, policy); assigned += part; vat += MoneyCalculator.Round(part * g.Key.Rate, policy); } }
        Guard.Require(vat >= 0 && input.SupplyAmount + vat > 0, "청구 금액을 확인해 주세요.");
        if (invoice.Status == "Confirmed") Guard.Require(others.Sum(x => x.SupplyAmount) + input.SupplyAmount <= order.SupplyAmount && others.Sum(x => x.VatAmount) + vat <= order.VatAmount && others.Sum(x => x.TotalAmount) + input.SupplyAmount + vat <= order.TotalAmount, "계약금액을 초과하는 청구입니다.", "OVER_INVOICE");
        var allocated = await db.Allocations.Where(x => x.InvoiceId == id && x.Status == "Active").SumAsync(x => x.Amount); Guard.Require(input.SupplyAmount + vat >= allocated, "청구 금액을 이미 배분된 입금액보다 작게 변경할 수 없습니다. 먼저 입금 배분을 취소해 주세요.");
        var old = Serialize(invoice); invoice.InvoiceType = input.InvoiceType; invoice.Date = input.Date; invoice.DueDate = input.DueDate; invoice.SupplyAmount = input.SupplyAmount; invoice.VatAmount = vat; invoice.TotalAmount = input.SupplyAmount + vat; Touch(invoice); Touch(order); Audit(invoice, "Update", old, Guard.Text(input.Reason, 1000)); Receipt(input.IdempotencyKey, "UpdateInvoice", id); if (invoice.Status == "Confirmed") await AutoCloseWhenSettled(order); return invoice;
    });
    public Task<Invoice> DeleteInvoice(long id, CommandMeta meta) => Write(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "DeleteInvoice"); var invoice = Guard.Found(await db.Invoices.FindAsync(id)); Guard.Version(invoice, meta.ExpectedVersion);
        Guard.Require(invoice.Status != "Deleted", "이미 삭제된 청구입니다."); Guard.Require(!string.IsNullOrWhiteSpace(meta.Reason), "삭제 사유를 입력해 주세요.");
        Guard.Require(!await db.Allocations.AnyAsync(x => x.InvoiceId == id && x.Status == "Active"), "연결된 입금 배분을 먼저 취소해 주세요.");
        var order = Guard.Found(await db.Orders.FindAsync(invoice.OrderId)); Guard.Require(order.Status == "Open", "종료된 수주를 먼저 재개해 주세요.");
        var old = Serialize(invoice); invoice.Status = "Deleted"; Touch(invoice); Touch(order); Audit(invoice, "Delete", old, Guard.Text(meta.Reason, 1000)); Receipt(meta.IdempotencyKey, "DeleteInvoice", id); return invoice;
    });
    public Task<Invoice> ConfirmInvoice(long id, CommandMeta meta) => Write(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "ConfirmInvoice"); var row = Guard.Found(await db.Invoices.FindAsync(id)); Guard.Version(row, meta.ExpectedVersion); Guard.Require(row.Status == "Draft", "초안 청구만 확정할 수 있습니다."); var o = Guard.Found(await db.Orders.FindAsync(row.OrderId)); Guard.Require(o.Status == "Open", "진행 중인 수주만 확정할 수 있습니다.");
        var others = await db.Invoices.Where(x => x.OrderId == o.Id && x.Status == "Confirmed").ToListAsync();
        Guard.Require(others.Sum(x => x.SupplyAmount) + row.SupplyAmount <= o.SupplyAmount && others.Sum(x => x.VatAmount) + row.VatAmount <= o.VatAmount && others.Sum(x => x.TotalAmount) + row.TotalAmount <= o.TotalAmount, "계약금액을 초과하는 청구입니다.", "OVER_INVOICE");
        var old = Serialize(row); row.Status = "Confirmed"; row.ConfirmedAtUtc = DateTime.UtcNow; Touch(row); Touch(o); Audit(row, "Confirm", old); Receipt(meta.IdempotencyKey, "ConfirmInvoice", id); await AutoCloseWhenSettled(o); return row;
    });
    public Task<Payment> RegisterPayment(PaymentInput input) => Write(async () =>
    {
        UserOnly();
        if (input.IdempotencyKey != null) { var receipt = await db.Receipts.FindAsync(input.IdempotencyKey); if (receipt != null) { Guard.Require(receipt.Operation == "CreatePayment", "이미 처리한 요청입니다.", "DUPLICATE_REQUEST"); return Guard.Found(await db.Payments.FindAsync(receipt.RecordId)); } await CheckKey(input.IdempotencyKey, "CreatePayment"); }
        var customer = Guard.Found(await db.Customers.FindAsync(input.CustomerId));
        if (input.OrderId is long orderId) { var target = Guard.Found(await db.Orders.FindAsync(orderId)); Guard.Require(target.Status == "Open" && target.CustomerId == customer.Id, "진행 중인 같은 거래처의 프로젝트를 선택해 주세요."); var financial = await Financial(orderId); Guard.Require(input.Amount <= financial.TotalRemaining, "입금액이 프로젝트의 남은 계약금액을 초과합니다."); }
        Guard.Require(input.Amount is > 0 and <= MoneyCalculator.MaxMoney, "입금액은 양수 원 단위로 입력해 주세요.");
        var row = new Payment { OrderId = input.OrderId, Status = "Confirmed", CustomerId = input.CustomerId, Date = input.Date, Amount = input.Amount, BankName = Guard.Text(input.BankName), Reference = Guard.Text(input.Reference), Memo = Guard.Text(input.Memo, 5000) }; db.Payments.Add(row); if (input.OrderId is long selectedOrder) Touch(Guard.Found(await db.Orders.FindAsync(selectedOrder))); await db.SaveChangesAsync(); Audit(row, "Create"); if (input.IdempotencyKey != null) Receipt(input.IdempotencyKey, "CreatePayment", row.Id); if (input.OrderId is long targetId) await AutoCloseWhenSettled(Guard.Found(await db.Orders.FindAsync(targetId))); return row;
    });
    public Task<Payment> UpdatePayment(long id, PaymentUpdateInput input) => Write(async () =>
    {
        UserOnly(); await CheckKey(input.IdempotencyKey, "UpdatePayment");
        var payment = Guard.Found(await db.Payments.FindAsync(id)); Guard.Version(payment, input.ExpectedVersion);
        Guard.Require(payment.Status is "Draft" or "Confirmed", "취소된 입금은 수정할 수 없습니다.");
        Guard.Require(input.Amount is > 0 and <= MoneyCalculator.MaxMoney, "입금액은 양수 원 단위로 입력해 주세요.");
        var hasAllocations = await db.Allocations.AnyAsync(x => x.PaymentId == id && x.Status == "Active");
        SalesOrder? target = null;
        if (hasAllocations)
        {
            Guard.Require(payment.OrderId == null && input.OrderId == null && input.Amount == payment.Amount, "기존 청구에 배분된 입금의 프로젝트나 금액을 변경하려면 배분을 먼저 취소해 주세요.");
            Guard.Require(payment.Status == "Confirmed", "이전 입금의 연결 상태를 확인해 주세요.");
        }
        else
        {
            Guard.Require(input.OrderId != null, "프로젝트를 선택해 주세요.");
            target = Guard.Found(await db.Orders.FindAsync(input.OrderId));
            Guard.Require(target.Status == "Open" && target.CustomerId == payment.CustomerId, "같은 거래처의 진행 중인 프로젝트를 선택해 주세요.");
            var remaining = (await Financial(target.Id)).TotalRemaining;
            if (payment.OrderId == target.Id && payment.Status == "Confirmed") remaining += payment.Amount;
            Guard.Require(input.Amount <= remaining, "입금액이 프로젝트의 남은 계약금액을 초과합니다.");
        }
        SalesOrder? previous = null;
        if (payment.OrderId is long previousId) { previous = Guard.Found(await db.Orders.FindAsync(previousId)); Guard.Require(previous.Status == "Open", "종료된 수주를 먼저 재개해 주세요."); }
        var old = Serialize(payment); payment.OrderId = input.OrderId; payment.Date = input.Date; payment.Amount = input.Amount;
        payment.BankName = Guard.Text(input.BankName ?? ""); payment.Reference = Guard.Text(input.Reference ?? ""); payment.Memo = Guard.Text(input.Memo ?? "", 5000);
        payment.Status = "Confirmed"; Touch(payment); if (previous != null) Touch(previous); if (target != null && previous?.Id != target.Id) Touch(target);
        Audit(payment, "Update", old); Receipt(input.IdempotencyKey, "UpdatePayment", id); if (target != null) await AutoCloseWhenSettled(target); return payment;
    });
    public Task<Payment> LinkPaymentProject(long id, PaymentProjectInput input) => Write(async () =>
    {
        UserOnly(); await CheckKey(input.IdempotencyKey, "LinkPaymentProject"); var payment = Guard.Found(await db.Payments.FindAsync(id)); Guard.Version(payment, input.ExpectedVersion);
        Guard.Require(payment.OrderId == null && payment.Status is "Draft" or "Confirmed", "연결할 수 없는 입금입니다.");
        Guard.Require(!await db.Allocations.AnyAsync(x => x.PaymentId == id && x.Status == "Active"), "이미 다른 청구에 연결된 입금은 연결 내역을 먼저 확인해 주세요.");
        var order = Guard.Found(await db.Orders.FindAsync(input.OrderId)); Guard.Require(order.Status == "Open" && order.CustomerId == payment.CustomerId, "같은 거래처의 진행 중인 프로젝트를 선택해 주세요.");
        Guard.Require(payment.Amount <= (await Financial(order.Id)).TotalRemaining, "입금액이 프로젝트의 남은 계약금액을 초과합니다.");
        var old = Serialize(payment); payment.OrderId = order.Id; payment.Status = "Confirmed"; Touch(payment); Touch(order); Audit(payment, "LinkProject", old); Receipt(input.IdempotencyKey, "LinkPaymentProject", id); await AutoCloseWhenSettled(order); return payment;
    });
    public Task<Payment> ConfirmPayment(long id, CommandMeta meta) => Write(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "ConfirmPayment"); var p = Guard.Found(await db.Payments.FindAsync(id)); Guard.Version(p, meta.ExpectedVersion); Guard.Require(p.Status == "Draft", "초안 입금만 확정할 수 있습니다."); var old = Serialize(p); p.Status = "Confirmed"; Touch(p); Audit(p, "Confirm", old); Receipt(meta.IdempotencyKey, "ConfirmPayment", id); return p;
    });
    public Task<Allocation> Allocate(AllocationInput input) => Write(async () =>
    {
        UserOnly(); await CheckKey(input.IdempotencyKey, "Allocate"); var p = Guard.Found(await db.Payments.FindAsync(input.PaymentId)); var i = Guard.Found(await db.Invoices.FindAsync(input.InvoiceId)); Guard.Version(p, input.ExpectedVersion); Guard.Version(i, input.InvoiceVersion);
        Guard.Require(p.OrderId == null && p.Status == "Confirmed" && i.Status == "Confirmed" && p.CustomerId == i.CustomerId, "같은 거래처의 확정 입금과 확정 청구만 배분할 수 있습니다.");
        Guard.Require(input.Amount <= (await Financial(i.OrderId)).TotalRemaining, "프로젝트 잔금을 초과해 연결할 수 없습니다.");
        var usedP = await db.Allocations.Where(x => x.PaymentId == p.Id && x.Status == "Active").SumAsync(x => x.Amount); var usedI = await db.Allocations.Where(x => x.InvoiceId == i.Id && x.Status == "Active").SumAsync(x => x.Amount);
        Guard.Require(input.Amount > 0 && input.Amount <= p.Amount - usedP && input.Amount <= i.TotalAmount - usedI, "입금 미배분액 또는 청구 미수금을 초과했습니다.", "OVER_ALLOCATION");
        var row = new Allocation { PaymentId = p.Id, InvoiceId = i.Id, Amount = input.Amount, IdempotencyKey = input.IdempotencyKey }; db.Allocations.Add(row); Touch(p); Touch(i); var o = Guard.Found(await db.Orders.FindAsync(i.OrderId)); Touch(o); await db.SaveChangesAsync(); Audit(row, "Allocate"); Receipt(input.IdempotencyKey, "Allocate", row.Id); await AutoCloseWhenSettled(o); return row;
    });
    public Task<Allocation> Reverse(long id, CommandMeta meta) => Write(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "ReverseAllocation"); var a = Guard.Found(await db.Allocations.FindAsync(id)); Guard.Version(a, meta.ExpectedVersion); Guard.Require(a.Status == "Active", "유효한 배분만 취소할 수 있습니다."); Guard.Require(!string.IsNullOrWhiteSpace(meta.Reason), "취소 사유를 입력해 주세요."); var old = Serialize(a); a.Status = "Reversed"; a.ReversedAtUtc = DateTime.UtcNow; Touch(a); Touch(Guard.Found(await db.Payments.FindAsync(a.PaymentId))); var i = Guard.Found(await db.Invoices.FindAsync(a.InvoiceId)); Touch(i); Touch(Guard.Found(await db.Orders.FindAsync(i.OrderId))); Audit(a, "Reverse", old, meta.Reason); Receipt(meta.IdempotencyKey, "ReverseAllocation", id); return a;
    });
    public Task<Record> Cancel(string type, long id, CommandMeta meta) => Write<Record>(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "Cancel"); Guard.Require(!string.IsNullOrWhiteSpace(meta.Reason), "취소 사유를 입력해 주세요."); Record row;
        if (type == "Invoice") { var i = Guard.Found(await db.Invoices.FindAsync(id)); Guard.Require(!await db.Allocations.AnyAsync(x => x.InvoiceId == id && x.Status == "Active"), "배분을 먼저 취소해 주세요."); Guard.Require((await db.Orders.FindAsync(i.OrderId))!.Status == "Open", "종료된 수주를 먼저 재개해 주세요."); row = i; }
        else if (type == "Payment") { var p = Guard.Found(await db.Payments.FindAsync(id)); Guard.Require(!await db.Allocations.AnyAsync(x => x.PaymentId == id && x.Status == "Active"), "배분을 먼저 취소해 주세요."); row = p; }
        else if (type == "Quotation") { Guard.Require(!await db.Orders.AnyAsync(x => x.QuotationId == id), "수주로 전환된 견적은 취소할 수 없습니다."); row = Guard.Found(await db.Quotations.FindAsync(id)); }
        else { throw new BusinessException("VALIDATION_ERROR", "취소할 문서 유형이 아닙니다."); }
        Guard.Version(row, meta.ExpectedVersion); var old = Serialize(row);
        if (row is Document d) { Guard.Require(d.Status != "Cancelled" && d.Status != "Deleted", "이미 취소되었거나 삭제된 문서입니다."); d.Status = "Cancelled"; if (d is Invoice i) Touch(Guard.Found(await db.Orders.FindAsync(i.OrderId))); }
        if (row is Payment pay) { Guard.Require(pay.Status != "Cancelled", "이미 취소된 입금입니다."); if (pay.OrderId is long linked) { var linkedOrder = Guard.Found(await db.Orders.FindAsync(linked)); Guard.Require(linkedOrder.Status == "Open", "종료된 수주를 먼저 재개해 주세요."); Touch(linkedOrder); } pay.Status = "Cancelled"; }
        Touch(row); Audit(row, "Cancel", old, meta.Reason); Receipt(meta.IdempotencyKey, "Cancel", id); return row;
    });
    public Task<Fulfillment> SaveFulfillment(Fulfillment input, long? id = null) => Write(async () =>
    {
        UserOnly();
        if (id == null && input.IdempotencyKey != null) { var receipt = await db.Receipts.FindAsync(input.IdempotencyKey); if (receipt != null) { Guard.Require(receipt.Operation == "CreateFulfillment", "이미 처리한 요청입니다.", "DUPLICATE_REQUEST"); return Guard.Found(await db.Fulfillments.FindAsync(receipt.RecordId)); } await CheckKey(input.IdempotencyKey, "CreateFulfillment"); }
        var o = Guard.Found(await db.Orders.FindAsync(input.OrderId)); Guard.Require(o.Status == "Open", "진행 중인 수주만 작업을 등록할 수 있습니다.");
        Guard.Require(input.Type is "Delivery" or "Installation" or "Programming" or "Commissioning" or "Inspection" or "Completion", "작업 종류를 확인해 주세요."); Guard.Require(input.Status is "Planned" or "InProgress" or "Completed" or "Cancelled", "작업 상태를 확인해 주세요."); Guard.Require(input.Quantity == null || input.Quantity > 0, "수량은 0보다 커야 합니다.");
        Fulfillment row; string? old = null;
        if (id != null) { row = Guard.Found(await db.Fulfillments.FindAsync(id)); Guard.Require(row.OrderId == input.OrderId, "수주는 변경할 수 없습니다."); Guard.Version(row, input.Version); old = Serialize(row); Touch(row); }
        else { row = new() { OrderId = o.Id }; db.Fulfillments.Add(row); }
        row.Type = input.Type; row.Description = Guard.Text(input.Description ?? "", 2000); row.PlannedDate = input.PlannedDate; row.CompletedDate = input.Status == "Completed" ? input.CompletedDate : null; row.Status = input.Status; row.Quantity = input.Quantity; row.Memo = Guard.Text(input.Memo, 5000); Touch(o); await db.SaveChangesAsync(); Audit(row, id == null ? "Create" : "Update", old); if (id == null && input.IdempotencyKey != null) Receipt(input.IdempotencyKey, "CreateFulfillment", row.Id); return row;
    });
    public Task<SalesOrder> CloseOrder(long id, CommandMeta meta) => Write(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "CloseOrder"); var o = Guard.Found(await db.Orders.FindAsync(id)); Guard.Version(o, meta.ExpectedVersion); Guard.Require(o.Status == "Open", "진행 중인 수주만 종료할 수 있습니다."); var f = await Financial(id); Guard.Require(f.TotalRemaining == 0, "전체 미수금이 남아 있어 수주를 종료할 수 없습니다."); Guard.Require(f.Uninvoiced == 0, "아직 청구하지 않은 금액이 있어 수주를 종료할 수 없습니다. 청구를 먼저 완료해 주세요."); var old = Serialize(o); o.Status = "Closed"; Touch(o); Audit(o, "Close", old); Receipt(meta.IdempotencyKey, "CloseOrder", id); return o;
    });
    public Task<SalesOrder> UpdateOrderDetails(long id, OrderDetailsInput input) => Write(async () =>
    {
        UserOnly(); var o = Guard.Found(await db.Orders.FindAsync(id)); Guard.Version(o, input.ExpectedVersion); Guard.Require(o.Status == "Open", "진행 중인 수주만 변경할 수 있습니다."); Guard.Require(input.DeliveryDate == null || input.DeliveryDate >= o.Date, "납기는 수주일 이후여야 합니다.");
        Guard.Require(input.PaymentTermsJson.Length <= 5000, "지급 조건이 너무 깁니다."); using var terms = JsonDocument.Parse(input.PaymentTermsJson); Guard.Require(terms.RootElement.ValueKind == JsonValueKind.Object, "지급 조건 형식을 확인해 주세요.");
        if (input.ProjectName != null) Guard.Require(!string.IsNullOrWhiteSpace(input.ProjectName), "프로젝트명을 입력해 주세요.");
        var old = Serialize(o); if (input.ProjectName != null) o.ProjectName = Guard.Text(input.ProjectName.Trim(), 300); o.DueDate = input.DeliveryDate; o.CustomerPONo = Guard.Text(input.CustomerPONo, 100); o.PaymentTermsJson = input.PaymentTermsJson; o.Memo = Guard.Text(input.Memo, 5000); Touch(o); Audit(o, "Update", old); return o;
    });
    public Task<SalesOrder> ChangeContract(long id, ContractChangeInput input) => Write(async () =>
    {
        UserOnly(); await CheckKey(input.IdempotencyKey, "ChangeContract"); var o = Guard.Found(await db.Orders.FindAsync(id)); Guard.Version(o, input.ExpectedVersion); Guard.Require(o.Status == "Open", "진행 중인 수주만 계약을 변경할 수 있습니다."); Guard.Require(!string.IsNullOrWhiteSpace(input.Reason), "계약 변경 사유가 필요합니다.");
        var amount = MoneyCalculator.Calculate(input.Items, input.Policy); var issued = await db.Invoices.Where(x => x.OrderId == id && x.Status == "Confirmed").ToListAsync();
        Guard.Require(amount.SupplyAmount >= issued.Sum(x => x.SupplyAmount) && amount.VatAmount >= issued.Sum(x => x.VatAmount) && amount.TotalAmount >= issued.Sum(x => x.TotalAmount), "확정 청구금액보다 계약금액을 줄일 수 없습니다.", "OVER_INVOICE");
        Guard.Require(amount.TotalAmount >= (await Financial(id)).Paid, "이미 입금된 금액보다 계약금액을 줄일 수 없습니다.");
        var old = Serialize(o); o.ItemsJson = Serialize(amount.Items); o.PolicyJson = Serialize(input.Policy); o.SupplyAmount = amount.SupplyAmount; o.VatAmount = amount.VatAmount; o.TotalAmount = amount.TotalAmount; Touch(o); Audit(o, "ContractChange", old, Guard.Text(input.Reason, 1000)); Receipt(input.IdempotencyKey, "ChangeContract", id); await AutoCloseWhenSettled(o); return o;
    });
    public Task<SalesOrder> ReopenOrder(long id, CommandMeta meta) => Write(async () =>
    {
        UserOnly(); await CheckKey(meta.IdempotencyKey, "ReopenOrder"); var o = Guard.Found(await db.Orders.FindAsync(id)); Guard.Version(o, meta.ExpectedVersion); Guard.Require(o.Status == "Closed" && !string.IsNullOrWhiteSpace(meta.Reason), "종료된 수주와 재개 사유를 확인해 주세요."); var old = Serialize(o); o.Status = "Open"; Touch(o); Audit(o, "Reopen", old, meta.Reason); Receipt(meta.IdempotencyKey, "ReopenOrder", id); return o;
    });
    public Task<Invoice> RecordTaxInvoice(long id, TaxInvoiceInput input) => Write(async () =>
    {
        UserOnly(); var i = Guard.Found(await db.Invoices.FindAsync(id)); Guard.Version(i, input.ExpectedVersion); Guard.Require(i.Status == "Confirmed", "확정 청구에만 발급 사실을 기록합니다."); Guard.Require(!input.Issued || input.Date != null && !string.IsNullOrWhiteSpace(input.ApprovalNo), "발급일과 승인번호를 입력해 주세요.");
        var old = Serialize(i); i.TaxInvoiceIssued = input.Issued; i.TaxInvoiceDate = input.Issued ? input.Date : null; i.TaxApprovalNo = input.Issued ? Guard.Text(input.ApprovalNo, 100) : ""; Touch(i); Audit(i, "TaxInvoiceRecord", old); return i;
    });
    public async Task<object> SuggestAllocation(long paymentId)
    {
        var p = Guard.Found(await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == paymentId)); Guard.Require(p.Status == "Confirmed", "확정 입금을 선택해 주세요.");
        var left = p.Amount - await db.Allocations.Where(x => x.PaymentId == p.Id && x.Status == "Active").SumAsync(x => x.Amount); var initial = left; var candidates = new List<object>();
        foreach (var i in await db.Invoices.AsNoTracking().Where(x => x.CustomerId == p.CustomerId && x.Status == "Confirmed").OrderBy(x => x.DueDate == null).ThenBy(x => x.DueDate).ThenBy(x => x.Id).ToListAsync())
        { var due = i.TotalAmount - await db.Allocations.Where(x => x.InvoiceId == i.Id && x.Status == "Active").SumAsync(x => x.Amount); var amount = Math.Min(due, left); if (amount > 0) { candidates.Add(new { invoiceId = i.Id, i.Number, amount, reason = "지급기한이 빠른 미수 청구부터 제안합니다. 사용자 승인 전에는 저장하지 않습니다." }); left -= amount; } }
        return new { paymentId, unallocated = initial, candidates, remaining = left };
    }
    public async Task<object> State()
    {
        var customers = await db.Customers.AsNoTracking().OrderByDescending(x => x.IsPreferred).ThenBy(x => x.CompanyName).ThenBy(x => x.Id).ToListAsync(); var orders = await db.Orders.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(); var financials = new List<FinancialStatus>(); foreach (var o in orders) financials.Add(await Financial(o.Id));
        return new { asOfUtc = DateTime.UtcNow, today = Today, customers, assets = await db.Assets.AsNoTracking().Where(x => x.Status != "Deleted").OrderByDescending(x => x.Id).ToListAsync(), quotations = await db.Quotations.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(), orders, invoices = await db.Invoices.AsNoTracking().Where(x => x.Status != "Deleted").OrderByDescending(x => x.Id).ToListAsync(), payments = await db.Payments.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(), purchases = await db.PurchaseOrders.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(), purchaseReceipts = await db.PurchaseReceipts.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(), purchaseBills = await db.PurchaseBills.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(), purchaseDisbursements = await db.PurchaseDisbursements.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(), allocations = await db.Allocations.AsNoTracking().ToListAsync(), fulfillments = await db.Fulfillments.AsNoTracking().ToListAsync(), financials, attachments = await db.Attachments.AsNoTracking().Where(x => x.Status == "Active").ToListAsync() };
    }
    static string Number(string prefix) => $"{prefix}-{Today:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}
