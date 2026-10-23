using System.Data;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Assets;

// Depreciation terms are dated. Method, rate and salvage changes apply from a month
// in an open period and recalculate only open posted months; a cost or in-use
// correction rebuilds the schedule only while every posted month is still open.
// A month posted after its year locked keeps its place in the schedule, but its
// charge is dated in the current open period.
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

    public Task<AssetPage> GetAssetPageAsync(string? query = null, bool includeDisposed = true,
        bool? afterDisposed = null, string? afterName = null, long? afterId = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200 || (afterDisposed is null) != (afterName is null) ||
            (afterDisposed is null) != (afterId is null))
            throw new DomainException(ErrorCode.Validation, "Invalid asset page filter.");
        string pattern = "%" + (query ?? string.Empty).Trim().Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return database.ReadAsync(connection =>
        {
            List<AssetItem> rows = connection.Query<AssetRow>("""
                WITH visible AS MATERIALIZED (
                    SELECT a.id,a.name,(a.disposed_date IS NOT NULL) AS disposed
                    FROM asset a
                    WHERE (@includeDisposed=1 OR a.disposed_date IS NULL)
                      AND (a.name LIKE @pattern ESCAPE '\' OR a.category LIKE @pattern ESCAPE '\')
                      AND (@afterDisposed IS NULL OR (a.disposed_date IS NOT NULL)>@afterDisposed
                           OR ((a.disposed_date IS NOT NULL)=@afterDisposed AND
                               (a.name COLLATE NOCASE>@afterName COLLATE NOCASE OR
                                (a.name COLLATE NOCASE=@afterName COLLATE NOCASE AND a.id>@afterId))))
                    ORDER BY disposed,a.name COLLATE NOCASE,a.id LIMIT @take
                )
                SELECT a.id AS Id,a.name AS Name,a.category AS Category,a.purchase_date AS PurchaseDate,
                       a.in_use_from AS InUseFrom,a.cost AS Cost,a.vendor_id AS VendorId,v.name AS VendorName,
                       a.method AS Method,a.rate_pct AS Rate,a.salvage_value AS SalvageValue,
                       a.disposed_date AS DisposedDate,a.disposal_value AS DisposalValue,a.notes AS Notes,
                       COALESCE((SELECT SUM(d.amount) FROM asset_depreciation d WHERE d.asset_id=a.id),0) AS Accumulated,
                       COALESCE((SELECT d.closing_value FROM asset_depreciation d WHERE d.asset_id=a.id
                                 ORDER BY d.period_month DESC LIMIT 1),a.cost) AS BookValue
                FROM visible page JOIN asset a ON a.id=page.id LEFT JOIN vendor v ON v.id=a.vendor_id
                ORDER BY page.disposed,page.name COLLATE NOCASE,page.id
                """, new { includeDisposed, pattern, afterDisposed, afterName, afterId, take = limit + 1 })
                .Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            AssetItem? last = rows.LastOrDefault();
            return new AssetPage(rows, hasMore, last is null ? null : last.DisposedDate is not null,
                last?.Name, last?.Id);
        }, cancellationToken);
    }

    public Task<long> SaveAssetAsync(long? id, AssetInput input, CancellationToken cancellationToken = default) =>
        SaveAssetAsync(id, input, null, cancellationToken);

    public Task<long> SaveAssetAsync(long? id, AssetInput input, YearMonth? effectiveFrom,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        return database.WriteAsync(transaction =>
        {
            DynamicParameters parameters = Parameters(id, input);
            if (id is null)
            {
                long created = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO asset(name,category,purchase_date,in_use_from,cost,vendor_id,method,rate_pct,salvage_value,notes)
                    VALUES (@name,@category,@purchaseDate,@inUseFrom,@cost,@vendorId,@method,@rate,@salvage,@notes) RETURNING id
                    """, parameters, transaction.Transaction);
                InsertTerm(transaction, created, YearMonth.From(input.InUseFrom), input, "INITIAL");
                audit.Write(transaction, "asset", created, "CREATE", new { input.Name, cost = input.Cost.Scaled, method = input.Method.ToString() });
                return created;
            }
            AssetState existing = LoadState(transaction.Connection, transaction.Transaction, id.Value);
            ChangePlan plan = Decide(transaction.Connection, transaction.Transaction, existing, input, effectiveFrom);
            if (plan.Treatment == "BLOCKED")
                throw new DomainException(ErrorCode.AssetTermsFinalized, plan.FirstOpenMonth.ToString());
            transaction.Connection.Execute("""
                UPDATE asset SET name=@name,category=@category,purchase_date=@purchaseDate,in_use_from=@inUseFrom,
                    cost=@cost,vendor_id=@vendorId,method=@method,rate_pct=@rate,salvage_value=@salvage,notes=@notes
                WHERE id=@id
                """, parameters, transaction.Transaction);
            int recalculated = 0;
            if (plan.Effective is { } effective && plan.DeleteFrom is { } deleteFrom)
            {
                List<PostedRow> posted = LoadPosted(transaction.Connection, transaction.Transaction, id.Value);
                YearMonth? through = posted.Count == 0 ? null : YearMonth.Parse(posted[^1].Period);
                recalculated = transaction.Connection.Execute(
                    "DELETE FROM asset_depreciation WHERE asset_id=@id AND period_month>=@month",
                    new { id, month = deleteFrom.ToString() }, transaction.Transaction);
                InsertTerm(transaction, id.Value, effective, input, plan.Treatment == "RECALCULATION" ? "RECALCULATION" : "CHANGE");
                if (through is { } target) PostAsset(transaction, id.Value, target, []);
            }
            audit.Write(transaction, "asset", id.Value, "UPDATE", new
            {
                input.Name,
                plan.Treatment,
                effective = plan.Effective?.ToString(),
                recalculatedMonths = recalculated,
                before = new { existing.Cost, existing.InUseFrom, existing.Method, existing.Rate, existing.Salvage },
                after = new
                {
                    cost = input.Cost.Scaled,
                    input.InUseFrom,
                    method = input.Method.ToString(),
                    rate = input.Rate.Scaled,
                    salvage = input.SalvageValue.Scaled
                },
            });
            return id.Value;
        }, cancellationToken);
    }

    // What saving the edit would do, without writing: which months recalculate and
    // the depreciation and book value before and after.
    public Task<AssetChangePreview> PreviewChangeAsync(long id, AssetInput input, YearMonth? effectiveFrom,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        return database.ReadAsync(connection =>
        {
            AssetState existing = LoadState(connection, null, id);
            ChangePlan plan = Decide(connection, null, existing, input, effectiveFrom);
            List<PostedRow> posted = LoadPosted(connection, null, id);
            Money bookBefore = Money.FromScaled(posted.Count == 0 ? existing.Cost : posted[^1].Closing);
            if (plan.Effective is not { } effective || plan.DeleteFrom is not { } deleteFrom)
                return new AssetChangePreview(plan.Treatment, plan.Effective, plan.FirstOpenMonth, 0, Money.Zero,
                    Money.Zero, bookBefore, plan.Treatment == "BLOCKED" ? bookBefore : bookBefore);
            List<PostedRow> kept = posted.Where(row => YearMonth.Parse(row.Period) < deleteFrom).ToList();
            List<PostedRow> replaced = posted.Where(row => YearMonth.Parse(row.Period) >= deleteFrom).ToList();
            List<TermRow> terms = LoadTerms(connection, null, id);
            terms.Add(TermFrom(long.MaxValue, effective, input));
            List<PlannedMonth> planned = replaced.Count == 0 ? [] :
                Plan(terms, kept, input.InUseFrom, existing.DisposedDate, YearMonth.Parse(replaced[^1].Period));
            long after = planned.Sum(static month => month.Amount);
            long bookAfter = planned.Count > 0 ? planned[^1].Closing : kept.Count > 0 ? kept[^1].Closing : input.Cost.Scaled;
            return new AssetChangePreview(plan.Treatment, effective, plan.FirstOpenMonth, replaced.Count,
                Money.FromScaled(replaced.Sum(static row => row.Amount)), Money.FromScaled(after), bookBefore,
                Money.FromScaled(bookAfter));
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AssetTermItem>> GetTermsAsync(long assetId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<AssetTermItem>)connection.Query<TermHistoryRow>("""
            SELECT id AS Id,effective_month AS EffectiveMonth,cost AS Cost,in_use_from AS InUseFrom,method AS Method,
                   rate_pct AS Rate,salvage_value AS Salvage,reason AS Reason,created_at AS CreatedAt
            FROM asset_term WHERE asset_id=@assetId ORDER BY id DESC LIMIT 100
            """, new { assetId }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    public Task<DepreciationPostResult> PostDueDepreciationAsync(CancellationToken cancellationToken = default)
    {
        YearMonth target = YearMonth.From(clock.BusinessDate).AddMonths(-1);
        return PostThroughAsync(target, cancellationToken);
    }

    public Task<DepreciationPostResult> PostThroughAsync(YearMonth target, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            List<long> assets = transaction.Connection.Query<long>(
                "SELECT id FROM asset WHERE in_use_from<=@targetEnd ORDER BY id",
                new { targetEnd = target.LastDay }, transaction.Transaction).ToList();
            var currentDated = new SortedSet<YearMonth>();
            int posted = 0;
            foreach (long asset in assets)
                posted += PostAsset(transaction, asset, target, currentDated);
            if (posted > 0)
                audit.Write(transaction, "asset_depreciation", 0, "POST_DUE", new
                {
                    target = target.ToString(),
                    rows = posted,
                    currentDated = currentDated.Select(static month => month.ToString()),
                });
            return new DepreciationPostResult(posted, currentDated.ToList());
        }, cancellationToken);

    public Task<IReadOnlyList<DepreciationEntry>> GetScheduleAsync(long assetId, CancellationToken cancellationToken = default)
    {
        if (assetId <= 0) throw new DomainException(ErrorCode.Validation, nameof(assetId));
        return database.ReadAsync(connection => (IReadOnlyList<DepreciationEntry>)connection.Query<DepreciationRow>("""
            SELECT id AS Id,asset_id AS AssetId,period_month AS Period,opening_value AS OpeningValue,
                   amount AS Amount,closing_value AS ClosingValue,posted_at AS PostedAt,pnl_date AS ChargedOn
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
            periods.EnsureOpen(transaction, date);
            AssetState asset = LoadState(transaction.Connection, transaction.Transaction, assetId);
            if (asset.DisposedDate is not null || date < asset.InUseFrom)
                throw new DomainException(ErrorCode.Validation, "Asset is already disposed or the date is before it entered use.");
            // Posted depreciation is immutable. A previously posted disposal month used the
            // whole month, so it cannot be turned into a partial month after the fact.
            string disposalPeriod = YearMonth.From(date).ToString();
            bool postedAtOrAfterDisposal = transaction.Connection.ExecuteScalar<long>("""
                SELECT EXISTS(SELECT 1 FROM asset_depreciation
                              WHERE asset_id=@assetId AND period_month>=@disposalPeriod)
                """, new { assetId, disposalPeriod }, transaction.Transaction) != 0;
            if (postedAtOrAfterDisposal)
                throw new DomainException(ErrorCode.Validation, "Depreciation is already posted for the disposal month or later.");
            PostAsset(transaction, assetId, YearMonth.From(date), [], date);
            long bookValue = transaction.Connection.ExecuteScalar<long?>("""
                SELECT closing_value FROM asset_depreciation WHERE asset_id=@assetId ORDER BY period_month DESC LIMIT 1
                """, new { assetId }, transaction.Transaction) ?? asset.Cost;
            transaction.Connection.Execute("""
                UPDATE asset SET disposed_date=@date,disposal_value=@proceeds,disposal_book_value=@bookValue
                WHERE id=@assetId
                """, new { assetId, date = Format(date), proceeds = proceeds.Scaled, bookValue }, transaction.Transaction);
            audit.Write(transaction, "asset", assetId, "DISPOSE", new { date = Format(date), proceeds = proceeds.Scaled, bookValue });
            OperationGuard.Complete(transaction, operationId, "asset", assetId);
        }, cancellationToken);
    }

    private ChangePlan Decide(IDbConnection connection, IDbTransaction? transaction, AssetState existing,
        AssetInput input, YearMonth? effectiveFrom)
    {
        YearMonth firstOpen = periods.FirstOpenMonth(connection, transaction);
        bool definition = input.Cost.Scaled != existing.Cost || input.InUseFrom != existing.InUseFrom;
        bool terms = MethodCode(input.Method) != existing.Method || input.Rate.Scaled != existing.Rate ||
            input.SalvageValue.Scaled != existing.Salvage;
        if (!definition && !terms) return new("DETAILS", null, null, firstOpen);
        if (existing.DisposedDate is not null)
            throw new DomainException(ErrorCode.Validation, "A disposed asset keeps its final terms.");
        List<PostedRow> posted = LoadPosted(connection, transaction, existing.Id);
        if (definition)
        {
            if (posted.Any(row => row.PnlDate is not null || YearMonth.Parse(row.Period) < firstOpen))
                return new("BLOCKED", null, null, firstOpen);
            YearMonth start = YearMonth.From(input.InUseFrom);
            YearMonth deleteFrom = posted.Count > 0 && YearMonth.Parse(posted[0].Period) < start
                ? YearMonth.Parse(posted[0].Period) : start;
            return new("RECALCULATION", start, deleteFrom, firstOpen);
        }
        YearMonth inUse = YearMonth.From(existing.InUseFrom);
        YearMonth earliest = firstOpen > inUse ? firstOpen : inUse;
        YearMonth effective = effectiveFrom ?? earliest;
        if (effective < firstOpen) throw new DomainException(ErrorCode.EffectiveMonthClosed, firstOpen.ToString());
        if (effective < inUse) effective = inUse;
        return new("TERMS", effective, effective, firstOpen);
    }

    // Posts the asset's unposted months through the target in schedule order.
    private int PostAsset(DbTx transaction, long assetId, YearMonth requestedTarget, SortedSet<YearMonth> currentDated,
        DateOnly? disposingOn = null)
    {
        AssetState asset = LoadState(transaction.Connection, transaction.Transaction, assetId);
        DateOnly? disposed = disposingOn ?? asset.DisposedDate;
        YearMonth target = disposed is { } date && YearMonth.From(date) < requestedTarget ? YearMonth.From(date) : requestedTarget;
        List<PostedRow> posted = LoadPosted(transaction.Connection, transaction.Transaction, assetId);
        List<PlannedMonth> planned = Plan(LoadTerms(transaction.Connection, transaction.Transaction, assetId),
            posted, asset.InUseFrom, disposed, target);
        foreach (PlannedMonth month in planned)
        {
            bool open = periods.IsOpen(transaction.Connection, transaction.Transaction, month.Period.LastDay);
            if (!open) currentDated.Add(month.Period);
            transaction.Connection.Execute("""
                INSERT INTO asset_depreciation(asset_id,period_month,opening_value,amount,closing_value,pnl_date)
                VALUES (@assetId,@period,@opening,@amount,@closing,@pnlDate)
                ON CONFLICT(asset_id,period_month) DO NOTHING
                """, new
            {
                assetId,
                period = month.Period.ToString(),
                opening = month.Opening,
                amount = month.Amount,
                closing = month.Closing,
                pnlDate = open ? null : Format(periods.Today),
            }, transaction.Transaction);
        }
        return planned.Count;
    }

    // The schedule after the kept months. Each month uses the newest term effective on
    // or before it; a term starting within a fiscal year trues up only its own days.
    private List<PlannedMonth> Plan(IReadOnlyList<TermRow> terms, IReadOnlyList<PostedRow> kept, DateOnly inUseFrom,
        DateOnly? disposed, YearMonth target)
    {
        int fyStartMonth = settings.Current.FyStartMonth;
        var history = kept.Select(static row => (Period: YearMonth.Parse(row.Period), row.Amount, row.Closing)).ToList();
        YearMonth next = history.Count == 0 ? YearMonth.From(inUseFrom) : history[^1].Period.AddMonths(1);
        long opening = history.Count == 0 ? TermFor(terms, next).Cost : history[^1].Closing;
        var planned = new List<PlannedMonth>();
        while (next <= target)
        {
            TermRow term = TermFor(terms, next);
            DateOnly fiscalFrom = FiscalYear.Range(FiscalYear.StartYear(next.FirstDay, fyStartMonth), fyStartMonth).From;
            DateOnly segmentFrom = Max(Max(fiscalFrom, inUseFrom), YearMonth.Parse(term.EffectiveMonth).FirstDay);
            YearMonth fiscalMonth = YearMonth.From(fiscalFrom);
            YearMonth segmentMonth = YearMonth.From(segmentFrom);
            long fiscalStartValue = term.Cost;
            long postedInSegment = 0;
            foreach ((YearMonth period, long amount, long closing) in history)
            {
                if (period < fiscalMonth) fiscalStartValue = closing;
                else if (period >= segmentMonth) postedInSegment += amount;
            }
            var info = new AssetDepreciationInfo(Money.FromScaled(term.Cost), Money.FromScaled(term.Salvage),
                Percent.FromScaled(term.Rate), ParseMethod(term.Method), segmentFrom, disposed);
            Money charge = Depreciation.MonthlyCharge(info, next, Money.FromScaled(opening),
                Money.FromScaled(fiscalStartValue), Money.FromScaled(postedInSegment), fyStartMonth);
            long closingValue = opening - charge.Scaled;
            planned.Add(new(next, opening, charge.Scaled, closingValue));
            history.Add((next, charge.Scaled, closingValue));
            opening = closingValue;
            next = next.AddMonths(1);
        }
        return planned;
    }

    // The newest term effective on or before the month; the earliest term covers months before any.
    private static TermRow TermFor(IReadOnlyList<TermRow> terms, YearMonth month)
    {
        string key = month.ToString();
        TermRow? match = null;
        foreach (TermRow term in terms)
            if (string.CompareOrdinal(term.EffectiveMonth, key) <= 0 && (match is null || term.Id > match.Id))
                match = term;
        return match ?? terms.OrderBy(static term => term.EffectiveMonth, StringComparer.Ordinal).First();
    }

    private static void InsertTerm(DbTx transaction, long assetId, YearMonth effective, AssetInput input, string reason) =>
        transaction.Connection.Execute("""
            INSERT INTO asset_term(asset_id,effective_month,cost,in_use_from,method,rate_pct,salvage_value,reason)
            VALUES (@assetId,@month,@cost,@inUseFrom,@method,@rate,@salvage,@reason)
            """, new
        {
            assetId,
            month = effective.ToString(),
            cost = input.Cost.Scaled,
            inUseFrom = Format(input.InUseFrom),
            method = MethodCode(input.Method),
            rate = input.Rate.Scaled,
            salvage = input.SalvageValue.Scaled,
            reason,
        }, transaction.Transaction);

    private static TermRow TermFrom(long id, YearMonth effective, AssetInput input) => new()
    {
        Id = id,
        EffectiveMonth = effective.ToString(),
        Cost = input.Cost.Scaled,
        Method = MethodCode(input.Method),
        Rate = input.Rate.Scaled,
        Salvage = input.SalvageValue.Scaled,
    };

    private static AssetState LoadState(IDbConnection connection, IDbTransaction? transaction, long id) =>
        connection.QuerySingleOrDefault<AssetState>("""
            SELECT id AS Id,cost AS Cost,in_use_from AS InUseFrom,method AS Method,rate_pct AS Rate,
                   salvage_value AS Salvage,disposed_date AS DisposedDate
            FROM asset WHERE id=@id
            """, new { id }, transaction) ?? throw new DomainException(ErrorCode.NotFound, "asset", id);

    private static List<PostedRow> LoadPosted(IDbConnection connection, IDbTransaction? transaction, long assetId) =>
        connection.Query<PostedRow>("""
            SELECT period_month AS Period,amount AS Amount,closing_value AS Closing,pnl_date AS PnlDate
            FROM asset_depreciation WHERE asset_id=@assetId ORDER BY period_month
            """, new { assetId }, transaction).ToList();

    private static List<TermRow> LoadTerms(IDbConnection connection, IDbTransaction? transaction, long assetId) =>
        connection.Query<TermRow>("""
            SELECT id AS Id,effective_month AS EffectiveMonth,cost AS Cost,method AS Method,rate_pct AS Rate,
                   salvage_value AS Salvage
            FROM asset_term WHERE asset_id=@assetId ORDER BY id
            """, new { assetId }, transaction).ToList();

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
        parameters.Add("method", MethodCode(input.Method));
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

    private static DateOnly Max(DateOnly left, DateOnly right) => left > right ? left : right;
    private static string MethodCode(DepreciationMethod method) => method == DepreciationMethod.Wdv ? "WDV" : "SLM";
    private static DepreciationMethod ParseMethod(string value) => value == "WDV" ? DepreciationMethod.Wdv : DepreciationMethod.Slm;
    private static string Required(string? value, int maximum) { string clean = value?.Trim() ?? ""; if (clean.Length == 0 || clean.Length > maximum) throw new DomainException(ErrorCode.Validation, "Required text is invalid."); return clean; }
    private static string? Optional(string? value, int maximum) { string? clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); if (clean?.Length > maximum) throw new DomainException(ErrorCode.Validation, "Text is too long."); return clean; }
    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ChangePlan(string Treatment, YearMonth? Effective, YearMonth? DeleteFrom, YearMonth FirstOpenMonth);
    private sealed record PlannedMonth(YearMonth Period, long Opening, long Amount, long Closing);

    private sealed class AssetRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? Category { get; init; } public DateOnly PurchaseDate { get; init; } public DateOnly InUseFrom { get; init; } public long Cost { get; init; } public long? VendorId { get; init; } public string? VendorName { get; init; } public string Method { get; init; } = ""; public long Rate { get; init; } public long SalvageValue { get; init; } public DateOnly? DisposedDate { get; init; } public long? DisposalValue { get; init; } public long Accumulated { get; init; } public long BookValue { get; init; } public string? Notes { get; init; } public AssetItem ToModel() => new(Id, Name, Category, PurchaseDate, InUseFrom, Money.FromScaled(Cost), VendorId, VendorName, ParseMethod(Method), Percent.FromScaled(Rate), Money.FromScaled(SalvageValue), DisposedDate, DisposalValue is null ? null : Money.FromScaled(DisposalValue.Value), Money.FromScaled(Accumulated), Money.FromScaled(BookValue), Notes); }
    private sealed class AssetState { public long Id { get; init; } public long Cost { get; init; } public DateOnly InUseFrom { get; init; } public string Method { get; init; } = ""; public long Rate { get; init; } public long Salvage { get; init; } public DateOnly? DisposedDate { get; init; } }
    private sealed class PostedRow { public string Period { get; init; } = ""; public long Amount { get; init; } public long Closing { get; init; } public string? PnlDate { get; init; } }
    private sealed class TermRow { public long Id { get; init; } public string EffectiveMonth { get; init; } = ""; public long Cost { get; init; } public string Method { get; init; } = ""; public long Rate { get; init; } public long Salvage { get; init; } }
    private sealed class TermHistoryRow { public long Id { get; init; } public string EffectiveMonth { get; init; } = ""; public long Cost { get; init; } public DateOnly InUseFrom { get; init; } public string Method { get; init; } = ""; public long Rate { get; init; } public long Salvage { get; init; } public string Reason { get; init; } = ""; public DateTime CreatedAt { get; init; } public AssetTermItem ToModel() => new(Id, YearMonth.Parse(EffectiveMonth), Money.FromScaled(Cost), InUseFrom, ParseMethod(Method), Percent.FromScaled(Rate), Money.FromScaled(Salvage), Reason, CreatedAt); }
    private sealed class DepreciationRow { public long Id { get; init; } public long AssetId { get; init; } public string Period { get; init; } = ""; public long OpeningValue { get; init; } public long Amount { get; init; } public long ClosingValue { get; init; } public DateTime PostedAt { get; init; } public DateOnly? ChargedOn { get; init; } public DepreciationEntry ToModel() => new(Id, AssetId, YearMonth.Parse(Period), Money.FromScaled(OpeningValue), Money.FromScaled(Amount), Money.FromScaled(ClosingValue), PostedAt, ChargedOn); }
}
