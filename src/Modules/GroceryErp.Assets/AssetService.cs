using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Assets;

public sealed class AssetService(
    DbExecutor database,
    ISettings settings,
    PeriodGuard periods,
    IAuditLog audit,
    IClock clock)
{
    public Task<IReadOnlyList<AssetItem>> GetAssetsAsync(bool includeDisposed = true, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<AssetItem>)connection.Query<AssetRow>("""
            SELECT a.id AS Id,a.name AS Name,a.category AS Category,a.purchase_date AS PurchaseDate,
                   a.in_use_from AS InUseFrom,a.cost AS Cost,a.vendor_id AS VendorId,v.name AS VendorName,
                   a.method AS Method,a.rate_pct AS Rate,a.salvage_value AS SalvageValue,
                   a.disposed_date AS DisposedDate,a.disposal_value AS DisposalValue,a.notes AS Notes,
                   COALESCE((SELECT SUM(d.amount) FROM asset_depreciation d WHERE d.asset_id=a.id),0) AS Accumulated,
                   COALESCE((SELECT d.closing_value FROM asset_depreciation d WHERE d.asset_id=a.id
                             ORDER BY d.period_month DESC LIMIT 1),a.cost) AS BookValue
            FROM asset a LEFT JOIN vendor v ON v.id=a.vendor_id
            WHERE @includeDisposed=1 OR a.disposed_date IS NULL
            ORDER BY a.disposed_date IS NOT NULL,a.name COLLATE NOCASE
            """, new { includeDisposed = includeDisposed ? 1 : 0 }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    public Task<long> SaveAssetAsync(long? id, AssetInput input, CancellationToken cancellationToken = default)
    {
        Validate(input);
        return database.WriteAsync(transaction =>
        {
            DynamicParameters parameters = Parameters(id, input);
            long result;
            if (id is null)
                result = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO asset(name,category,purchase_date,in_use_from,cost,vendor_id,method,rate_pct,salvage_value,notes)
                    VALUES (@name,@category,@purchaseDate,@inUseFrom,@cost,@vendorId,@method,@rate,@salvage,@notes) RETURNING id
                    """, parameters, transaction.Transaction);
            else
            {
                result = id.Value;
                int changed = transaction.Connection.Execute("""
                    UPDATE asset SET name=@name,category=@category,purchase_date=@purchaseDate,in_use_from=@inUseFrom,
                        cost=@cost,vendor_id=@vendorId,method=@method,rate_pct=@rate,salvage_value=@salvage,notes=@notes
                    WHERE id=@id AND disposed_date IS NULL
                      AND NOT EXISTS (SELECT 1 FROM asset_depreciation WHERE asset_id=asset.id)
                    """, parameters, transaction.Transaction);
                if (changed == 0) throw new DomainException(ErrorCode.Validation, "Posted or disposed assets cannot be edited.");
            }
            audit.Write(transaction, "asset", result, id is null ? "CREATE" : "UPDATE", new { input.Name, cost = input.Cost.Scaled, method = input.Method.ToString() });
            return result;
        }, cancellationToken);
    }

    public Task<DepreciationPostResult> PostDueDepreciationAsync(CancellationToken cancellationToken = default)
    {
        YearMonth target = YearMonth.From(clock.BusinessDate).AddMonths(-1);
        return PostThroughAsync(target, cancellationToken);
    }

    public Task<DepreciationPostResult> PostThroughAsync(YearMonth target, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            List<AssetCalcRow> assets = transaction.Connection.Query<AssetCalcRow>("""
                SELECT id AS Id,cost AS Cost,salvage_value AS Salvage,rate_pct AS Rate,method AS Method,
                       in_use_from AS InUseFrom,disposed_date AS DisposedDate
                FROM asset WHERE in_use_from<=@targetEnd ORDER BY id
                """, new { targetEnd = target.LastDay }, transaction.Transaction).ToList();
            var locked = new SortedSet<YearMonth>();
            int posted = 0;
            foreach (AssetCalcRow asset in assets)
                posted += PostAssetThrough(transaction, asset, target, locked);
            if (posted > 0) audit.Write(transaction, "asset_depreciation", 0, "POST_DUE", new { target = target.ToString(), rows = posted });
            return new DepreciationPostResult(posted, locked.ToList());
        }, cancellationToken);

    public Task<IReadOnlyList<DepreciationEntry>> GetScheduleAsync(long assetId, CancellationToken cancellationToken = default)
    {
        if (assetId <= 0) throw new DomainException(ErrorCode.Validation, nameof(assetId));
        return database.ReadAsync(connection => (IReadOnlyList<DepreciationEntry>)connection.Query<DepreciationRow>("""
            SELECT id AS Id,asset_id AS AssetId,period_month AS Period,opening_value AS OpeningValue,
                   amount AS Amount,closing_value AS ClosingValue,posted_at AS PostedAt
            FROM asset_depreciation WHERE asset_id=@assetId ORDER BY period_month
            """, new { assetId }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public Task DisposeAsync(long assetId, DateOnly date, Money proceeds, string operationId, CancellationToken cancellationToken = default)
    {
        if (assetId <= 0 || proceeds < Money.Zero) throw new DomainException(ErrorCode.Validation, "Invalid disposal.");
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.AssetDispose);
            if (replay is not null) return;
            periods.EnsureOpen(date);
            AssetCalcRow? asset = transaction.Connection.QuerySingleOrDefault<AssetCalcRow>("""
                SELECT id AS Id,cost AS Cost,salvage_value AS Salvage,rate_pct AS Rate,method AS Method,
                       in_use_from AS InUseFrom,disposed_date AS DisposedDate
                FROM asset WHERE id=@assetId
                """, new { assetId }, transaction.Transaction);
            if (asset is null) throw new DomainException(ErrorCode.NotFound, "asset", assetId);
            if (asset.DisposedDate is not null || date < asset.InUseFrom)
                throw new DomainException(ErrorCode.Validation, "Asset is already disposed or the date is before it entered use.");
            asset.DisposedDate = date;
            var locked = new SortedSet<YearMonth>();
            PostAssetThrough(transaction, asset, YearMonth.From(date), locked);
            if (locked.Count > 0) throw new DomainException(ErrorCode.PeriodLocked, locked.First().LastDay, settings.Current.BooksLockedUntil!);
            transaction.Connection.Execute(
                "UPDATE asset SET disposed_date=@date,disposal_value=@proceeds WHERE id=@assetId",
                new { assetId, date = Format(date), proceeds = proceeds.Scaled }, transaction.Transaction);
            audit.Write(transaction, "asset", assetId, "DISPOSE", new { date = Format(date), proceeds = proceeds.Scaled });
            OperationGuard.Complete(transaction, operationId, "asset", assetId);
        }, cancellationToken);
    }

    private int PostAssetThrough(DbTx transaction, AssetCalcRow asset, YearMonth requestedTarget, SortedSet<YearMonth> locked)
    {
        YearMonth target = asset.DisposedDate is { } disposed && YearMonth.From(disposed) < requestedTarget
            ? YearMonth.From(disposed) : requestedTarget;
        DepreciationState? latest = transaction.Connection.QuerySingleOrDefault<DepreciationState>("""
            SELECT period_month AS Period,closing_value AS Closing FROM asset_depreciation
            WHERE asset_id=@id ORDER BY period_month DESC LIMIT 1
            """, new { id = asset.Id }, transaction.Transaction);
        YearMonth next = latest is null ? YearMonth.From(asset.InUseFrom) : YearMonth.Parse(latest.Period).AddMonths(1);
        Money opening = latest is null ? Money.FromScaled(asset.Cost) : Money.FromScaled(latest.Closing);
        var info = new AssetDepreciationInfo(Money.FromScaled(asset.Cost), Money.FromScaled(asset.Salvage),
            Percent.FromScaled(asset.Rate), ParseMethod(asset.Method), asset.InUseFrom, asset.DisposedDate);
        int count = 0;
        while (next <= target)
        {
            if (settings.Current.BooksLockedUntil is { } lockDate && next.LastDay <= lockDate)
            {
                locked.Add(next);
                break;
            }
            int fiscalStartYear = FiscalYear.StartYear(next.FirstDay, settings.Current.FyStartMonth);
            (DateOnly fiscalFrom, _) = FiscalYear.Range(fiscalStartYear, settings.Current.FyStartMonth);
            long? priorClosing = transaction.Connection.QuerySingleOrDefault<long?>("""
                SELECT closing_value FROM asset_depreciation
                WHERE asset_id=@id AND period_month<@fiscalFrom ORDER BY period_month DESC LIMIT 1
                """, new { id = asset.Id, fiscalFrom = YearMonth.From(fiscalFrom).ToString() }, transaction.Transaction);
            Money fiscalStartValue = priorClosing is null ? Money.FromScaled(asset.Cost) : Money.FromScaled(priorClosing.Value);
            long postedScaled = transaction.Connection.ExecuteScalar<long>("""
                SELECT COALESCE(SUM(amount),0) FROM asset_depreciation
                WHERE asset_id=@id AND period_month BETWEEN @from AND @through
                """, new { id = asset.Id, from = YearMonth.From(fiscalFrom).ToString(), through = next.AddMonths(-1).ToString() }, transaction.Transaction);
            Money charge = Depreciation.MonthlyCharge(info, next, opening, fiscalStartValue, Money.FromScaled(postedScaled), settings.Current.FyStartMonth);
            Money closing = opening - charge;
            transaction.Connection.Execute("""
                INSERT INTO asset_depreciation(asset_id,period_month,opening_value,amount,closing_value)
                VALUES (@id,@period,@opening,@amount,@closing)
                ON CONFLICT(asset_id,period_month) DO NOTHING
                """, new { id = asset.Id, period = next.ToString(), opening = opening.Scaled, amount = charge.Scaled, closing = closing.Scaled }, transaction.Transaction);
            opening = closing;
            count++;
            next = next.AddMonths(1);
        }
        return count;
    }

    private static DynamicParameters Parameters(long? id, AssetInput input)
    {
        var parameters = new DynamicParameters();
        parameters.Add("id", id);
        parameters.Add("name", Required(input.Name, 150));
        parameters.Add("category", Optional(input.Category, 100));
        parameters.Add("purchaseDate", Format(input.PurchaseDate));
        parameters.Add("inUseFrom", Format(input.InUseFrom));
        parameters.Add("cost", input.Cost.Scaled);
        parameters.Add("vendorId", input.VendorId);
        parameters.Add("method", input.Method == DepreciationMethod.Wdv ? "WDV" : "SLM");
        parameters.Add("rate", input.Rate.Scaled);
        parameters.Add("salvage", input.SalvageValue.Scaled);
        parameters.Add("notes", Optional(input.Notes, 500));
        return parameters;
    }

    private static void Validate(AssetInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Required(input.Name, 150);
        if (input.PurchaseDate > input.InUseFrom || input.Cost <= Money.Zero || input.SalvageValue < Money.Zero ||
            input.SalvageValue > input.Cost || input.Rate < Percent.Zero || input.Rate > new Percent(100) || input.VendorId <= 0)
            throw new DomainException(ErrorCode.Validation, "Invalid asset.");
    }

    private static DepreciationMethod ParseMethod(string value) => value == "WDV" ? DepreciationMethod.Wdv : DepreciationMethod.Slm;
    private static string Required(string? value, int maximum) { string clean = value?.Trim() ?? ""; if (clean.Length == 0 || clean.Length > maximum) throw new DomainException(ErrorCode.Validation, "Required text is invalid."); return clean; }
    private static string? Optional(string? value, int maximum) { string? clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); if (clean?.Length > maximum) throw new DomainException(ErrorCode.Validation, "Text is too long."); return clean; }
    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class AssetRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? Category { get; init; } public DateOnly PurchaseDate { get; init; } public DateOnly InUseFrom { get; init; } public long Cost { get; init; } public long? VendorId { get; init; } public string? VendorName { get; init; } public string Method { get; init; } = ""; public long Rate { get; init; } public long SalvageValue { get; init; } public DateOnly? DisposedDate { get; init; } public long? DisposalValue { get; init; } public long Accumulated { get; init; } public long BookValue { get; init; } public string? Notes { get; init; } public AssetItem ToModel() => new(Id, Name, Category, PurchaseDate, InUseFrom, Money.FromScaled(Cost), VendorId, VendorName, ParseMethod(Method), Percent.FromScaled(Rate), Money.FromScaled(SalvageValue), DisposedDate, DisposalValue is null ? null : Money.FromScaled(DisposalValue.Value), Money.FromScaled(Accumulated), Money.FromScaled(BookValue), Notes); }
    private sealed class AssetCalcRow { public long Id { get; init; } public long Cost { get; init; } public long Salvage { get; init; } public long Rate { get; init; } public string Method { get; init; } = ""; public DateOnly InUseFrom { get; init; } public DateOnly? DisposedDate { get; set; } }
    private sealed class DepreciationState { public string Period { get; init; } = ""; public long Closing { get; init; } }
    private sealed class DepreciationRow { public long Id { get; init; } public long AssetId { get; init; } public string Period { get; init; } = ""; public long OpeningValue { get; init; } public long Amount { get; init; } public long ClosingValue { get; init; } public DateTime PostedAt { get; init; } public DepreciationEntry ToModel() => new(Id, AssetId, YearMonth.Parse(Period), Money.FromScaled(OpeningValue), Money.FromScaled(Amount), Money.FromScaled(ClosingValue), PostedAt); }
}
