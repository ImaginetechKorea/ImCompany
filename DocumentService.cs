using System.Net;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace ImOrder;

public class DocumentService(AppDb db, SettingsStore settings)
{
    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    public async Task<string> Html(string type, long id)
    {
        Document d = type switch { "SalesOrder" => Guard.Found(await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)), "Invoice" => Guard.Found(await db.Invoices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)), _ => throw new BusinessException("VALIDATION_ERROR", "출력할 수 없는 문서입니다.") };
        var c = BusinessService.Deserialize<Customer>(d.SnapshotJson); var config = settings.Read(); var items = BusinessService.Deserialize<List<CalculatedItem>>(d.ItemsJson); string title = type == "SalesOrder" ? "수 주 확 인 서" : "청 구 서";
        var b = new StringBuilder($"<!doctype html><html lang='ko'><meta charset='utf-8'><title>{E(d.Number)}</title><link rel='stylesheet' href='/print.css'><body><aside>브라우저 인쇄에서 ‘PDF로 저장’을 선택할 수 있습니다. <button id='print'>인쇄 / PDF 저장</button></aside><h1>{title}</h1><header><div><h2>{E(c.CompanyName)} 귀하</h2><p>{E(c.Address)}</p><p>담당 {E(c.ContactName)} · {E(c.Phone)}</p></div><div><h2>{E(config.CompanyName)}</h2><p>{E(config.BusinessNumber)}</p><p>{E(config.Address)}</p><p>{E(config.Phone)}</p></div></header><p>문서번호: {E(d.Number)}　 작성일: {d.Date}　 상태: {E(d.Status)}</p><h2>{E(d.ProjectName)}</h2><p>현장: {E(d.SiteName)}　 기한: {d.DueDate}</p><div class='total'>합계 금액　 ₩ {d.TotalAmount:N0}</div><table><thead><tr><th>품목 / 규격</th><th>수량</th><th>단가</th><th>공급가액</th><th>VAT</th><th>합계</th></tr></thead><tbody>");
        foreach (var i in items) b.Append($"<tr><td>{E(i.Input.Description)}<small>{E(i.Input.Specification)} · {E(i.Input.TaxCategory)}</small></td><td>{i.Input.Quantity:N4} {E(i.Input.Unit)}</td><td>{i.Input.UnitPrice:N4}</td><td>{i.SupplyAmount:N0}</td><td>{i.VatAmount:N0}</td><td>{i.TotalAmount:N0}</td></tr>");
        if (items.Count == 0) b.Append($"<tr><td>{E(d.ProjectName)} ({E(d is Invoice invoice ? invoice.InvoiceType : "")})</td><td>1</td><td>{d.SupplyAmount:N0}</td><td>{d.SupplyAmount:N0}</td><td>{d.VatAmount:N0}</td><td>{d.TotalAmount:N0}</td></tr>");
        b.Append($"</tbody><tfoot><tr><th colspan='3'>합계</th><th>{d.SupplyAmount:N0}</th><th>{d.VatAmount:N0}</th><th>{d.TotalAmount:N0}</th></tr></tfoot></table><p class='memo'>{E(d.Memo)}</p><footer>KRW · 내부 업무 문서 · 전자세금계산서가 아닙니다.</footer><script src='/print.js'></script></body></html>"); return b.ToString();
    }
    public async Task<string> PurchaseHtml(long id)
    {
        var po = Guard.Found(await db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id));
        var supplier = BusinessService.Deserialize<Customer>(po.SupplierSnapshotJson); var company = settings.Read(); var items = BusinessService.Deserialize<List<CalculatedItem>>(po.ItemsJson);
        var b = new StringBuilder($"<!doctype html><html lang='ko'><meta charset='utf-8'><title>{E(po.Number)}</title><link rel='stylesheet' href='/print.css'><body><aside>브라우저 인쇄에서 ‘PDF로 저장’을 선택할 수 있습니다. <button id='print'>인쇄 / PDF 저장</button></aside><h1>발 주 서</h1><header><div><h2>{E(supplier.CompanyName)} 귀하</h2><p>{E(supplier.Address)}</p><p>담당 {E(supplier.ContactName)} · {E(supplier.Phone)}</p></div><div><h2>{E(company.CompanyName)}</h2><p>{E(company.BusinessNumber)}</p><p>{E(company.Address)}</p><p>{E(company.Phone)}</p></div></header><p>발주번호: {E(po.Number)}　발주일: {po.Date}　납기: {po.DueDate?.ToString() ?? "미정"}　상태: {(po.Status == "Cancelled" ? "취소" : "진행")}</p><h2>{E(po.Title)}</h2><div class='total'>합계 금액　₩ {po.TotalAmount:N0}</div><table><thead><tr><th>품목 / 규격</th><th>수량</th><th>단가</th><th>공급가액</th><th>VAT</th><th>합계</th></tr></thead><tbody>");
        foreach (var i in items) b.Append($"<tr><td>{E(i.Input.Description)}<small>{E(i.Input.Specification)} · {E(i.Input.Unit)}</small></td><td>{i.Input.Quantity:N4}</td><td>{i.Input.UnitPrice:N4}</td><td>{i.SupplyAmount:N0}</td><td>{i.VatAmount:N0}</td><td>{i.TotalAmount:N0}</td></tr>");
        b.Append($"</tbody><tfoot><tr><th colspan='3'>합계</th><th>{po.SupplyAmount:N0}</th><th>{po.VatAmount:N0}</th><th>{po.TotalAmount:N0}</th></tr></tfoot></table><p class='memo'>{E(po.Memo)}</p><footer>KRW · 발주서 · 세금계산서가 아닙니다.</footer><script src='/print.js'></script></body></html>"); return b.ToString();
    }
    public async Task<byte[]> Excel(string kind)
    {
        using var book = new XLWorkbook(); var sheet = book.AddWorksheet("업무 현황");
        var headers = new[] { "번호", "거래처", "프로젝트 / 내용", "일자", "상태", "공급가액", "VAT", "합계" };
        for (int i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i]; int row = 2;
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.CompanyName);
        if (kind == "customers") { foreach (var c in await db.Customers.AsNoTracking().ToListAsync()) { var values = new[] { c.Code, c.CompanyName, c.ContactName, c.Phone, c.BusinessNumber, c.Address, c.Email, c.Memo }; for (int col = 0; col < values.Length; col++) sheet.Cell(row, col + 1).Value = values[col]; row++; } var hh = new[] { "코드", "거래처", "담당자", "전화", "사업자번호", "주소", "이메일", "메모" }; for (int i = 0; i < hh.Length; i++) sheet.Cell(1, i + 1).Value = hh[i]; }
        else if (kind == "payments") { sheet.Cell(1, 3).Value = "프로젝트 / 입금자"; var orderNames = await db.Orders.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.ProjectName); foreach (var p in await db.Payments.AsNoTracking().ToListAsync()) { sheet.Cell(row, 1).Value = p.Id; sheet.Cell(row, 2).Value = customers[p.CustomerId]; sheet.Cell(row, 3).Value = p.OrderId is long projectId && orderNames.TryGetValue(projectId, out var projectName) ? projectName : p.Reference; sheet.Cell(row, 4).Value = p.Date.ToString(); sheet.Cell(row, 5).Value = p.Status; sheet.Cell(row, 8).Value = p.Amount; row++; } }
        else
        {
            List<Document> docs = kind switch { "orders" => (await db.Orders.AsNoTracking().ToListAsync()).Cast<Document>().ToList(), "invoices" => (await db.Invoices.AsNoTracking().Where(x => x.Status != "Deleted").ToListAsync()).Cast<Document>().ToList(), _ => throw new BusinessException("VALIDATION_ERROR", "지원하지 않는 내보내기 종류입니다.") };
            foreach (var d in docs) { sheet.Cell(row, 1).Value = d.Number; sheet.Cell(row, 2).Value = customers[d.CustomerId]; sheet.Cell(row, 3).Value = d.ProjectName; sheet.Cell(row, 4).Value = d.Date.ToString(); sheet.Cell(row, 5).Value = d.Status; sheet.Cell(row, 6).Value = d.SupplyAmount; sheet.Cell(row, 7).Value = d.VatAmount; sheet.Cell(row, 8).Value = d.TotalAmount; row++; }
        }
        var range = sheet.Range(1, 1, Math.Max(row - 1, 1), 8); range.Style.Font.FontName = "맑은 고딕"; range.Style.Font.FontSize = 11; sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("173E35"); sheet.Row(1).Style.Font.FontColor = XLColor.White; sheet.Row(1).Style.Font.Bold = true; sheet.SheetView.FreezeRows(1); sheet.Columns().AdjustToContents(12, 65); sheet.Column(3).Width = 45; range.Style.Alignment.WrapText = true; if (kind != "customers") sheet.Columns(6, 8).Style.NumberFormat.Format = "#,##0"; range.SetAutoFilter(); sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape; sheet.PageSetup.FitToPages(1, 0);
        using var stream = new MemoryStream(); book.SaveAs(stream); return stream.ToArray();
    }
    // 사용자 선택에 따른 세무사 전달용 검토자료이며 신고 확정 명세서가 아닙니다.
    public async Task<byte[]> AssetExpenseExcel(int year)
    {
        Guard.Require(year is >= 1900 and <= 9998, "과세연도를 확인해 주세요.");
        var start = new DateOnly(year, 1, 1); var end = new DateOnly(year + 1, 1, 1);
        var assets = await db.Assets.AsNoTracking().Where(a => a.Status != "Deleted" && a.DepreciationMethod == "ImmediateExpense" && a.DepreciationStartDate >= start && a.DepreciationStartDate < end).OrderBy(a => a.DepreciationStartDate).ThenBy(a => a.Id).ToListAsync();
        var evidence = (await db.Attachments.AsNoTracking().Where(x => x.EntityType == "Asset" && x.Status == "Active").ToListAsync()).GroupBy(x => x.EntityId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.OriginalFileName)));
        using var book = new XLWorkbook(); var sheet = book.AddWorksheet("즉시상각 검토");
        sheet.Cell(1, 1).Value = $"{year}년 귀속 자산 필요경비 검토 목록 · 세무사 전달용"; sheet.Range(1, 1, 1, 16).Merge().Style.Font.Bold = true;
        sheet.Cell(2, 1).Value = "사용자가 선택한 검토 목록입니다. 필요경비 인정 여부·계상 시기와 금액은 증빙 및 신고 방식에 따라 세무사와 확인하세요. 증빙 원본은 별도로 전달해 주세요."; sheet.Range(2, 1, 2, 16).Merge().Style.Alignment.WrapText = true; sheet.Row(2).Height = 32;
        string[] headers = ["자산명", "분류", "모델", "구입일", "사용 시작일", "구입처", "증빙 종류", "총 결제금액", "공급가액", "부가세", "매입세액 공제 여부", "업무사용 비율(%)", "취득금액", "즉시상각 검토금액", "첨부파일명", "메모"];
        for (int i = 0; i < headers.Length; i++) sheet.Cell(4, i + 1).Value = headers[i]; int row = 5;
        foreach (var a in assets)
        {
            sheet.Cell(row, 1).Value = a.Name; sheet.Cell(row, 2).Value = a.Category; sheet.Cell(row, 3).Value = a.Model;
            sheet.Cell(row, 4).Value = a.PurchaseDate?.ToString("yyyy-MM-dd") ?? ""; sheet.Cell(row, 5).Value = a.DepreciationStartDate?.ToString("yyyy-MM-dd") ?? "";
            sheet.Cell(row, 6).Value = a.Vendor; sheet.Cell(row, 7).Value = a.EvidenceType;
            if (a.TotalPaid is long total) sheet.Cell(row, 8).Value = total;
            if (a.SupplyValue is long supply) sheet.Cell(row, 9).Value = supply;
            if (a.VatValue is long vat) sheet.Cell(row, 10).Value = vat;
            sheet.Cell(row, 11).Value = a.InputVatDeductible is bool deduct ? (deduct ? "공제" : "공제 안 함") : "미입력";
            if (a.BusinessUseBps is int bps) sheet.Cell(row, 12).Value = bps / 100m;
            sheet.Cell(row, 13).Value = a.PurchaseAmount; if (a.ImmediateExpenseAmount is long expense) sheet.Cell(row, 14).Value = expense;
            sheet.Cell(row, 15).Value = evidence.GetValueOrDefault(a.Id, "없음"); sheet.Cell(row, 16).Value = a.Memo; row++;
        }
        sheet.Cell(row, 13).Value = "검토금액 합계"; sheet.Cell(row, 14).Value = assets.Sum(a => a.ImmediateExpenseAmount ?? 0);
        sheet.Row(4).Style.Font.Bold = true; sheet.Row(4).Style.Fill.BackgroundColor = XLColor.FromHtml("173E35"); sheet.Row(4).Style.Font.FontColor = XLColor.White;
        sheet.Columns(8, 10).Style.NumberFormat.Format = "#,##0"; sheet.Columns(13, 14).Style.NumberFormat.Format = "#,##0"; sheet.SheetView.FreezeRows(4); sheet.Columns().AdjustToContents(12, 40); sheet.Column(15).Width = 35; sheet.Column(16).Width = 35;
        using var stream = new MemoryStream(); book.SaveAs(stream); return stream.ToArray();
    }

    // 읽기용 엑셀 사본입니다. 첨부파일과 복원용 원본 백업은 별도로 보관합니다.
    public async Task<byte[]> ExcelBackup()
    {
        using var transaction = await db.Database.BeginTransactionAsync();
        using var book = new XLWorkbook();
        var names = new Dictionary<string, string> {
            ["Vendor"]="구입처", ["EvidenceType"]="증빙 종류", ["TotalPaid"]="총 결제금액", ["SupplyValue"]="공급가액", ["VatValue"]="부가세", ["InputVatDeductible"]="매입세액 공제", ["BusinessUseBps"]="업무사용 비율(0.01%)", ["ImmediateExpenseAmount"]="즉시상각 검토금액", ["DepreciationMethod"]="상각 방법", ["DepreciationRateBps"]="연 상각률(0.01%)", ["DepreciationStartDate"]="사용 시작일", ["UsefulLifeYears"]="내용연수(년)", ["ResidualValue"]="잔존가액", ["Name"]="자산명", ["Category"]="분류", ["Model"]="모델명", ["SerialNumber"]="일련번호", ["PurchaseDate"]="구입일", ["PurchaseAmount"]="구입금액", ["Location"]="보관 장소", ["Custodian"]="담당자", ["Id"]="고유번호", ["SupplierId"]="공급처 고유번호", ["SalesOrderId"]="연결 수주 고유번호", ["Title"]="발주명", ["PurchaseOrderId"]="발주 고유번호", ["SupplierSnapshotJson"]="공급처 저장내역", ["Version"]="버전", ["CreatedAtUtc"]="등록일시(UTC)", ["UpdatedAtUtc"]="수정일시(UTC)", ["Code"]="코드", ["CompanyName"]="거래처명", ["BusinessNumber"]="사업자번호", ["CeoName"]="대표자", ["BusinessType"]="업태", ["BusinessItem"]="종목", ["Address"]="주소", ["ContactName"]="담당자", ["Phone"]="전화", ["Email"]="이메일", ["Memo"]="메모", ["IsActive"]="사용 중", ["IsPreferred"]="주거래처", ["Number"]="문서번호", ["CustomerId"]="거래처 고유번호", ["ProjectName"]="프로젝트명", ["SiteName"]="현장명", ["Date"]="일자", ["DueDate"]="기한", ["SupplyAmount"]="공급가액", ["VatAmount"]="부가세", ["TotalAmount"]="합계", ["Status"]="상태", ["SnapshotJson"]="거래처 저장내역", ["ItemsJson"]="품목 상세", ["PolicyJson"]="금액 계산 기준", ["QuotationId"]="견적 고유번호", ["CustomerPONo"]="고객 발주번호", ["PaymentTermsJson"]="지급 조건", ["OrderId"]="수주 고유번호", ["InvoiceType"]="청구 유형", ["TaxInvoiceIssued"]="세금계산서 발급 기록", ["TaxInvoiceDate"]="세금계산서 작성일", ["TaxApprovalNo"]="세금계산서 승인번호", ["ConfirmedAtUtc"]="확정일시(UTC)", ["Type"]="작업 종류", ["PlannedDate"]="예정일", ["CompletedDate"]="완료일", ["Description"]="작업 내용", ["Quantity"]="수량", ["Amount"]="금액", ["BankName"]="은행", ["AccountMasked"]="계좌", ["Reference"]="입금자·참조", ["PaymentId"]="입금 고유번호", ["InvoiceId"]="청구 고유번호", ["ReversedAtUtc"]="배분취소일시(UTC)"
        };
        void AddSheet<T>(string title, IEnumerable<T> rows)
        {
            var sheet = book.AddWorksheet(title);
            var properties = typeof(T).GetProperties().Where(p => names.ContainsKey(p.Name)).ToArray();
            for (var c = 0; c < properties.Length; c++) sheet.Cell(1, c + 1).Value = names[properties[c].Name];
            var row = 2;
            foreach (var item in rows)
            {
                for (var col = 0; col < properties.Length; col++)
                {
                    var value = properties[col].GetValue(item); var cell = sheet.Cell(row, col + 1);
                    if (value is null) continue;
                    if (value is long n) { cell.Value = n; if (properties[col].Name.EndsWith("Amount") || properties[col].Name == "Amount") cell.Style.NumberFormat.Format = "#,##0"; }
                    else if (value is int i) cell.Value = i;
                    else if (value is decimal d) cell.Value = d;
                    else if (value is bool b) cell.Value = b ? "예" : "아니요";
                    else { var text = value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss") : value.ToString() ?? ""; if (text.Length > 32767) throw new BusinessException("VALIDATION_ERROR", "엑셀 셀의 길이 제한을 초과한 자료가 있습니다. 복원용 백업을 이용해 주세요."); cell.Value = text; }
                }
                row++;
            }
            sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("173E35"); sheet.Row(1).Style.Font.FontColor = XLColor.White; sheet.Row(1).Style.Font.Bold = true;
            sheet.SheetView.FreezeRows(1); sheet.Range(1, 1, Math.Max(1, row - 1), properties.Length).SetAutoFilter(); sheet.Columns().AdjustToContents(12, 45);
        }
        var info = book.AddWorksheet("안내"); info.Cell("A1").Value = "ImCompany 엑셀 업무자료"; info.Cell("A1").Style.Font.Bold = true; info.Cell("A2").Value = "생성일시: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); info.Cell("A3").Value = "이 엑셀 파일에는 업무자료와 복원 데이터가 함께 들어 있습니다."; info.Cell("A4").Value = "복원 데이터가 유지되도록 백업 파일은 엑셀에서 편집하지 마세요."; info.Column(1).Width = 90;
        AddSheet("자산", await db.Assets.AsNoTracking().Where(x => x.Status != "Deleted").ToListAsync());
        AddSheet("거래처", await db.Customers.AsNoTracking().ToListAsync());
        AddSheet("수주", await db.Orders.AsNoTracking().ToListAsync());
        AddSheet("납품·작업", await db.Fulfillments.AsNoTracking().ToListAsync());
        AddSheet("청구", await db.Invoices.AsNoTracking().ToListAsync());
        AddSheet("입금", await db.Payments.AsNoTracking().ToListAsync());
        AddSheet("입금 배분", await db.Allocations.AsNoTracking().ToListAsync());
        AddSheet("발주", await db.PurchaseOrders.AsNoTracking().ToListAsync());
        AddSheet("입고", await db.PurchaseReceipts.AsNoTracking().ToListAsync());
        AddSheet("매입", await db.PurchaseBills.AsNoTracking().ToListAsync());
        AddSheet("지급", await db.PurchaseDisbursements.AsNoTracking().ToListAsync());
        await transaction.CommitAsync(); using var stream = new MemoryStream(); book.SaveAs(stream); return stream.ToArray();
    }

    public async Task<string> SaveExcelBackup(string folder, StorageService storage, DataPaths paths)
    {
        var bytes = await ExcelBackup();
        var snapshot = Path.Combine(paths.Temp, await storage.Backup(false, paths.Temp));
        try
        {
            using var memory = new MemoryStream(); await memory.WriteAsync(bytes);
            using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Update, true))
            { var entry = archive.CreateEntry("imcompany/restore-data.zip", System.IO.Compression.CompressionLevel.NoCompression); using var destination = entry.Open(); using var source = File.OpenRead(snapshot); await source.CopyToAsync(destination); }
            bytes = memory.ToArray();
        }
        finally { if (File.Exists(snapshot)) File.Delete(snapshot); }
        var name = $"ImCompany_Excel_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.xlsx";
        var target = Path.Combine(folder, name); var temporary = target + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes); File.Move(temporary, target); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return name;
    }

}
