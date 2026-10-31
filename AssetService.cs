using Microsoft.EntityFrameworkCore;

namespace ImOrder;

public record AssetInput(string Name, string Category, string Model, string SerialNumber, DateOnly? PurchaseDate, long PurchaseAmount, string Location, string Custodian, string Status, long? OrderId, string Memo, int ExpectedVersion, DateOnly? DepreciationStartDate = null, int? UsefulLifeYears = null, long ResidualValue = 0, string DepreciationMethod = "None", int? DepreciationRateBps = null, string Vendor = "", string EvidenceType = "", long? TotalPaid = null, long? SupplyValue = null, long? VatValue = null, bool? InputVatDeductible = null, int? BusinessUseBps = null, long? ImmediateExpenseAmount = null);
public record AssetDeleteInput(int ExpectedVersion);
public class AssetService(AppDb db, BusinessService business)
{
    public Task<Asset> Save(AssetInput input, long? id = null) => business.Write(async () =>
    {
        Guard.Require(!string.IsNullOrWhiteSpace(input.Name), "자산명을 입력해 주세요.");
        Guard.Require(input.PurchaseAmount >= 0, "구입금액은 0원 이상이어야 합니다.");
        Guard.Require(input.ResidualValue >= 0 && input.ResidualValue <= input.PurchaseAmount, "잔존가액은 0원 이상 구입금액 이하여야 합니다.");
        Guard.Require(input.DepreciationMethod is "None" or "StraightLine" or "DecliningBalance" or "ImmediateExpense", "상각 방법을 확인해 주세요.");
        Guard.Require(input.DepreciationMethod == "None" || input.DepreciationStartDate != null, "상각하려면 사용 시작일을 입력해 주세요.");
        Guard.Require(input.DepreciationMethod != "ImmediateExpense" || input.ImmediateExpenseAmount is > 0 && input.ImmediateExpenseAmount <= input.PurchaseAmount, "즉시상각 검토 금액은 0원 초과 취득금액 이하로 입력해 주세요.");
        Guard.Require(input.DepreciationMethod != "StraightLine" || input.UsefulLifeYears != null, "정액법의 내용연수를 입력해 주세요.");
        Guard.Require(input.DepreciationMethod != "DecliningBalance" || input.DepreciationRateBps is > 0 and <= 10000, "정률법의 연 상각률은 0% 초과 100% 이하로 입력해 주세요.");
        Guard.Require(input.UsefulLifeYears is null or >= 1 and <= 100, "내용연수는 1년부터 100년까지 입력해 주세요.");
        Guard.Require(input.DepreciationStartDate == null || input.PurchaseDate == null || input.DepreciationStartDate >= input.PurchaseDate, "사용 시작일은 구입일보다 빠를 수 없습니다.");
        Guard.Require(input.TotalPaid is null or >= 0 && input.SupplyValue is null or >= 0 && input.VatValue is null or >= 0, "증빙 금액은 0원 이상이어야 합니다.");
        Guard.Require(input.BusinessUseBps is null or >= 0 and <= 10000, "업무 사용 비율은 0%부터 100%까지 입력해 주세요.");
        Guard.Require(input.Status is "InUse" or "Stored" or "Repair" or "Disposed", "자산 상태를 확인해 주세요.");
        if (input.OrderId.HasValue) Guard.Require(await db.Orders.AnyAsync(x => x.Id == input.OrderId.Value), "연결할 프로젝트를 찾을 수 없습니다.");
        Asset asset; string? previous = null;
        if (id.HasValue) { asset = Guard.Found(await db.Assets.FindAsync(id.Value)); Guard.Require(asset.Status != "Deleted", "삭제된 자산입니다."); Guard.Version(asset, input.ExpectedVersion); previous = BusinessService.Serialize(asset); asset.Version++; asset.UpdatedAtUtc = DateTime.UtcNow; }
        else { asset = new Asset(); db.Assets.Add(asset); }
        asset.Name = Guard.Text(input.Name, 200); asset.Category = Guard.Text(input.Category ?? "", 100); asset.Model = Guard.Text(input.Model ?? "", 200); asset.SerialNumber = Guard.Text(input.SerialNumber ?? "", 200);
        asset.Vendor = Guard.Text(input.Vendor ?? "", 200); asset.EvidenceType = Guard.Text(input.EvidenceType ?? "", 100); asset.TotalPaid = input.TotalPaid; asset.SupplyValue = input.SupplyValue; asset.VatValue = input.VatValue; asset.InputVatDeductible = input.InputVatDeductible; asset.BusinessUseBps = input.BusinessUseBps;
        asset.PurchaseDate = input.PurchaseDate; asset.PurchaseAmount = input.PurchaseAmount; asset.DepreciationStartDate = input.DepreciationStartDate; asset.UsefulLifeYears = input.UsefulLifeYears; asset.ResidualValue = input.ResidualValue; asset.DepreciationMethod = input.DepreciationMethod; asset.ImmediateExpenseAmount = input.DepreciationMethod == "ImmediateExpense" ? input.ImmediateExpenseAmount : null; asset.DepreciationRateBps = input.DepreciationMethod == "DecliningBalance" ? input.DepreciationRateBps : null; asset.Location = Guard.Text(input.Location ?? "", 200); asset.Custodian = Guard.Text(input.Custodian ?? "", 200); asset.Status = input.Status; asset.OrderId = input.OrderId; asset.Memo = Guard.Text(input.Memo ?? "", 5000);
        await db.SaveChangesAsync(); business.Audit(asset, id.HasValue ? "Update" : "Create", previous); return asset;
    });
    public Task<object> Delete(long id, AssetDeleteInput input) => business.Write<object>(async () =>
    {
        var asset = Guard.Found(await db.Assets.FindAsync(id)); Guard.Require(asset.Status != "Deleted", "이미 삭제된 자산입니다."); Guard.Version(asset, input.ExpectedVersion);
        var previous = BusinessService.Serialize(asset); asset.Status = "Deleted"; asset.Version++; asset.UpdatedAtUtc = DateTime.UtcNow; business.Audit(asset, "Delete", previous); return new { deleted = true };
    });
}
