using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Inventory;

/// <summary>A tax rate as it is at the moment a record saves it: its ID, name and percent.</summary>
public sealed record TaxRateSnapshot(long? Id, string? Label, Percent Rate)
{
    /// <summary>No tax rate: no label and nothing charged.</summary>
    public static TaxRateSnapshot None { get; } = new(null, null, Percent.Zero);
}

/// <summary>
/// A lot's purchase-side tax, saved once when the lot is created and never changed: the rate it was
/// bought at and one piece's purchase price before tax, its tax and its price including tax.
/// <see cref="PurchaseUnitCost.Booked"/> is the lot's unit cost.
/// </summary>
public sealed record LotTax(TaxRateSnapshot Rate, PurchaseUnitCost Cost)
{
    /// <summary>
    /// Adds the five product_lot tax columns' values as @taxRateId, @taxLabel, @taxRatePct,
    /// @unitTax and @unitCostInclTax.
    /// </summary>
    public DynamicParameters AddTo(DynamicParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Add("taxRateId", Rate.Id);
        parameters.Add("taxLabel", Rate.Label);
        parameters.Add("taxRatePct", Rate.Rate.Scaled);
        parameters.Add("unitTax", Cost.Tax.Scaled);
        parameters.Add("unitCostInclTax", Cost.InclTax.Scaled);
        return parameters;
    }
}

/// <summary>Resolves tax rates and the tax values every lot writer saves.</summary>
public static class LotTaxes
{
    /// <summary>
    /// Gets whether purchase tax is part of cost: in Composition and No-GST modes there is no input
    /// credit. Read inside the writer's transaction, so it is the setting the document is saved under.
    /// </summary>
    public static bool TaxIsCost(DbTx transaction)
    {
        string? mode = transaction.Connection.ExecuteScalar<string?>(
            "SELECT value FROM setting WHERE key='tax.mode'", transaction: transaction.Transaction);
        return mode is not null && !string.Equals(mode, "REGULAR", StringComparison.Ordinal);
    }

    /// <summary>Reads a tax rate's current name and percent; null is no rate.</summary>
    public static TaxRateSnapshot Rate(DbTx transaction, long? taxRateId, bool activeOnly = false)
    {
        if (taxRateId is null) return TaxRateSnapshot.None;
        RateRow row = transaction.Connection.QuerySingleOrDefault<RateRow>("""
            SELECT id AS Id,name AS Name,rate_pct AS RatePct,is_active AS IsActive FROM tax_rate WHERE id=@taxRateId
            """, new { taxRateId }, transaction.Transaction)
            ?? throw new DomainException(ErrorCode.NotFound, "tax rate", taxRateId);
        if (activeOnly && !row.IsActive)
            throw new DomainException(ErrorCode.TaxRateInactive, row.Name);
        return new(row.Id, row.Name, Percent.FromScaled(row.RatePct));
    }

    /// <summary>Reads the product's tax rate as it is now; none when the product has no rate.</summary>
    public static TaxRateSnapshot ProductRate(DbTx transaction, long productId)
    {
        long? taxRateId = transaction.Connection.ExecuteScalar<long?>(
            "SELECT tax_rate_id FROM product WHERE id=@productId", new { productId }, transaction.Transaction);
        return Rate(transaction, taxRateId);
    }

    /// <summary>A lot typed with its purchase price before tax, such as a direct lot.</summary>
    public static LotTax FromBeforeTax(DbTx transaction, TaxRateSnapshot rate, Money beforeTax)
    {
        ArgumentNullException.ThrowIfNull(rate);
        return new(rate, LandedCost.FromBeforeTax(beforeTax, rate.Rate, TaxIsCost(transaction)));
    }

    /// <summary>A lot whose booked cost is already fixed, such as a recreated lot.</summary>
    public static LotTax FromBooked(DbTx transaction, TaxRateSnapshot rate, Money booked)
    {
        ArgumentNullException.ThrowIfNull(rate);
        return new(rate, LandedCost.FromBooked(booked, rate.Rate, TaxIsCost(transaction)));
    }

    private sealed class RateRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public long RatePct { get; init; }
        public bool IsActive { get; init; }
    }
}
