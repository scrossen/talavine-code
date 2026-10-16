using System.Globalization;

namespace SwarmCore.Agent.Business;

public sealed record BusinessProductSelection(Guid OfferId, int Quantity);
public sealed record BusinessAssemblyComponent(long ProductId, long InventoryItemId, int QuantityPerUnit);
/// <summary>Explicit commercial scope, frozen before customer acceptance; never inferred from prose.</summary>
public sealed record BusinessProductScope(Guid OfferId, string Sku, int Quantity, string UnitPrice, string Currency,
    string FulfillmentKind = "Consumable", BusinessAssemblyComponent[]? Components = null, string? SerialNumber = null)
{
    public decimal Total => decimal.Parse(UnitPrice, CultureInfo.InvariantCulture) * Quantity;
    public void Validate()
    {
        if (OfferId == Guid.Empty || string.IsNullOrWhiteSpace(Sku) || Sku.Length > 50 || Quantity is < 1 or > 10000
            || !decimal.TryParse(UnitPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price < 0 || price > 1000000
            || decimal.Round(price, 2) != price || Currency.Length != 3 || !Currency.All(char.IsAsciiLetter))
            throw new ArgumentException("Freeze an explicit product offer, SKU, positive quantity, bounded unit price and currency.");
        if (FulfillmentKind is not ("Consumable" or "Assembly" or "HardAsset")
            || FulfillmentKind == "HardAsset" && (Quantity != 1 || string.IsNullOrWhiteSpace(SerialNumber) || SerialNumber.Length > 50)
            || FulfillmentKind == "Assembly" && (Components is not { Length: > 0 and <= 100 }
                || Components.Any(x => x.ProductId <= 0 || x.InventoryItemId <= 0 || x.QuantityPerUnit is < 1 or > 10000)
                || Components.Select(x => x.InventoryItemId).Distinct().Count() != Components.Length))
            throw new ArgumentException("Freeze an explicit supported fulfillment kind, complete bounded BOM or exact serialized asset.");
    }
}
