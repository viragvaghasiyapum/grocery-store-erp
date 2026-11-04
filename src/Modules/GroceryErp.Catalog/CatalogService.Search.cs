using System.Globalization;
using Dapper;
using GroceryErp.Core.Errors;

namespace GroceryErp.Catalog;

public sealed partial class CatalogService
{
    /// <summary>Most products one "all matching" bulk action may resolve; larger sets must be narrowed.</summary>
    public const int MaxBulkProducts = 5000;

    public async Task<IReadOnlyList<ProductListItem>> SearchAsync(ProductSearchFilter filter, CancellationToken cancellationToken = default) =>
        (await SearchPageAsync(filter, null, null, cancellationToken).ConfigureAwait(false)).Items;

    public Task<ProductSearchPage> SearchPageAsync(ProductSearchFilter filter, string? afterName = null,
        long? afterId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (afterName is null != (afterId is null))
            throw new ArgumentException("Both product cursor fields are required.");
        int limit = Math.Clamp(filter.Limit, 1, 200);
        string query = ValidateFilter(filter);
        return database.ReadAsync(connection =>
        {
            var parameters = new DynamicParameters();
            AddFilterParameters(parameters, filter);
            parameters.Add("afterName", afterName);
            parameters.Add("afterId", afterId);

            // An exact SKU or barcode is the operator's clearest intent, so it leads the first
            // page; later pages exclude it so keyset paging never repeats or skips a row.
            long[] exactIds = filter.ExactFirst && query.Length > 0 ? ExactMatches(connection, query, filter) : [];
            int nameLimit = afterName is null ? Math.Max(limit - exactIds.Length, 1) : limit;
            parameters.Add("limit", nameLimit + 1);
            parameters.Add("exactIds", exactIds);
            string exclude = exactIds.Length == 0 ? string.Empty : "AND p.id NOT IN @exactIds";

            string join = string.Empty;
            string search = string.Empty;
            bool nameOrderedTerm = false;
            if (UsesFts(query))
            {
                string match = FtsMatch(query);
                parameters.Add("match", match);
                string shortTerms = ShortTermConditions(query, parameters);
                // A common term walks names in order and keeps the matching rows, rather than
                // sorting every match; a rare one starts from its few matches.
                nameOrderedTerm = CommonTerm(connection, filter, match);
                if (nameOrderedTerm)
                    search = "AND p.id IN (SELECT rowid FROM product_fts WHERE product_fts MATCH @match)" + shortTerms;
                else
                {
                    join = "JOIN product_fts f ON f.rowid=p.id";
                    search = "AND product_fts MATCH @match" + shortTerms;
                }
            }
            else if (query.Length > 0)
            {
                search = PrefixCondition;
                parameters.Add("prefix", PrefixPattern(query));
            }

            bool broadLowStock = filter.LowStockOnly && query.Length == 0 && filter.CategoryId is null &&
                filter.BrandId is null && !filter.NoBarcodeOnly && !filter.MissingLocalNameOnly && !filter.LooseOnly &&
                filter.ExpiryFilter == "ALL" && filter.ProductClass is null && !filter.HasStockOnly &&
                filter.VendorId is null && !filter.ArchivedOnly;
            bool nameOrderedLowStock = broadLowStock && connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM product_stock_total WHERE is_low=1") > 500;
            string productIndex = nameOrderedLowStock || nameOrderedTerm ? "INDEXED BY ix_product_name" : string.Empty;
            string stockJoin = filter.LowStockOnly
                ? (nameOrderedLowStock ? "CROSS JOIN" : "JOIN") +
                  " product_stock_total stock ON stock.product_id=p.id AND stock.is_low=1"
                : string.Empty;

            // The cursor as a name bound the index can seek to, so a deep page costs what the first does.
            string after = afterName is null ? string.Empty : """
                AND p.name COLLATE NOCASE >= @afterName COLLATE NOCASE
                AND (p.name COLLATE NOCASE > @afterName COLLATE NOCASE OR p.id>@afterId)
                """;
            List<ProductListItem> rows = connection.Query<ProductListItem>(ListSql($$"""
                SELECT p.id,p.name FROM product p {{productIndex}} {{join}} {{stockJoin}}
                WHERE {{FilterConditions(filter)}} {{after}}
                  {{search}} {{exclude}}
                ORDER BY p.name COLLATE NOCASE,p.id LIMIT @limit
                """), parameters).AsList();
            bool hasMore = rows.Count > nameLimit;
            if (hasMore) rows.RemoveAt(nameLimit);
            ProductListItem? last = rows.LastOrDefault();
            if (afterName is null && exactIds.Length > 0)
            {
                List<ProductListItem> exact = connection.Query<ProductListItem>(ListSql("""
                    SELECT p.id,p.name FROM product p WHERE p.id IN @exactIds
                    ORDER BY p.name COLLATE NOCASE,p.id
                    """), new { exactIds, expiryToday = ExpiryToday(filter) }).AsList();
                rows.InsertRange(0, exact);
            }
            return new ProductSearchPage(rows, hasMore, last?.Name, last?.Id);
        }, cancellationToken);
    }

    /// <summary>Counts every product matching the filters, for an "all matching" selection.</summary>
    public Task<int> CountAsync(ProductSearchFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        string query = ValidateFilter(filter);
        return database.ReadAsync(connection =>
        {
            (string sql, DynamicParameters parameters) = MatchingSql(filter, query, "COUNT(*)", string.Empty);
            return connection.ExecuteScalar<int>(sql, parameters);
        }, cancellationToken);
    }

    /// <summary>
    /// Resolves the IDs of every product matching the filters, in list order, for a reviewed bulk
    /// action. Fails rather than truncating when more than <paramref name="maximum"/> match.
    /// </summary>
    public Task<IReadOnlyList<long>> ResolveIdsAsync(ProductSearchFilter filter, int maximum = MaxBulkProducts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        string query = ValidateFilter(filter);
        int cap = Math.Clamp(maximum, 1, MaxBulkProducts);
        return database.ReadAsync(connection =>
        {
            (string sql, DynamicParameters parameters) = MatchingSql(filter, query, "p.id",
                "ORDER BY p.name COLLATE NOCASE,p.id LIMIT @cap");
            parameters.Add("cap", cap + 1);
            long[] ids = connection.Query<long>(sql, parameters).ToArray();
            if (ids.Length > cap) throw new DomainException(ErrorCode.TooManyRows, cap);
            return (IReadOnlyList<long>)ids;
        }, cancellationToken);
    }

    private static string ValidateFilter(ProductSearchFilter filter)
    {
        if (filter.ExpiryFilter is not ("ALL" or "EXPIRED" or "EXPIRING"))
            throw new DomainException(ErrorCode.Validation, "Invalid expiry filter.");
        if (filter.ProductClass is not (null or "SALEABLE" or "INTERNAL"))
            throw new DomainException(ErrorCode.Validation, "Invalid product classification.");
        return CatalogText.Optional(filter.Query, 120) ?? string.Empty;
    }

    private void AddFilterParameters(DynamicParameters parameters, ProductSearchFilter filter)
    {
        parameters.Add("CategoryId", filter.CategoryId);
        parameters.Add("BrandId", filter.BrandId);
        parameters.Add("NoBarcodeOnly", filter.NoBarcodeOnly);
        parameters.Add("MissingLocalNameOnly", filter.MissingLocalNameOnly);
        parameters.Add("IncludeArchived", filter.IncludeArchived || filter.ArchivedOnly);
        parameters.Add("ArchivedOnly", filter.ArchivedOnly);
        parameters.Add("SaleableOnly", filter.SaleableOnly);
        parameters.Add("ProductClass", filter.ProductClass);
        parameters.Add("LooseOnly", filter.LooseOnly);
        parameters.Add("HasStockOnly", filter.HasStockOnly);
        parameters.Add("VendorId", filter.VendorId);
        parameters.Add("expiryToday", ExpiryToday(filter));
        parameters.Add("expirySoon", ExpiryAsOf(filter).AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    // Expiry marks and filters follow the business date, as the lot filters and badges do.
    private DateOnly ExpiryAsOf(ProductSearchFilter filter) => filter.ExpiryAsOf ?? clock.BusinessDate;

    private string ExpiryToday(ProductSearchFilter filter) =>
        ExpiryAsOf(filter).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Shared by the page, count and ID resolution so a bulk action always means exactly the rows listed.
    private static string FilterConditions(ProductSearchFilter filter)
    {
        string expiry = filter.ExpiryFilter switch
        {
            "EXPIRED" => "AND EXISTS(SELECT 1 FROM product_lot el WHERE el.product_id=p.id AND el.qty_on_hand>0 AND el.expiry_date<@expiryToday)",
            "EXPIRING" => "AND EXISTS(SELECT 1 FROM product_lot el WHERE el.product_id=p.id AND el.qty_on_hand>0 AND el.expiry_date>=@expiryToday AND el.expiry_date<=@expirySoon)",
            _ => string.Empty,
        };
        string vendor = filter.VendorId is null ? string.Empty
            : "AND p.id IN (SELECT vl.product_id FROM product_lot vl WHERE vl.vendor_id=@VendorId)";
        string stock = filter.HasStockOnly
            ? "AND EXISTS(SELECT 1 FROM product_stock_total hs WHERE hs.product_id=p.id AND hs.quantity>0)"
            : string.Empty;
        // Only the filters in use appear, as plain comparisons the indexes can serve; status and
        // class are validated literals so the small archived and internal-use name indexes qualify.
        var conditions = new List<string>
        {
            filter.ArchivedOnly ? "p.is_active=0" : filter.IncludeArchived ? "1=1" : "p.is_active=1",
        };
        if (filter.SaleableOnly) conditions.Add("p.product_class='SALEABLE'");
        if (filter.ProductClass is "SALEABLE" or "INTERNAL") conditions.Add($"p.product_class='{filter.ProductClass}'");
        if (filter.CategoryId is not null) conditions.Add("p.category_id=@CategoryId");
        if (filter.BrandId is not null) conditions.Add("p.brand_id=@BrandId");
        if (filter.NoBarcodeOnly)
            conditions.Add("NOT EXISTS(SELECT 1 FROM product_barcode pb WHERE pb.product_id=p.id AND pb.lot_id IS NULL)");
        if (filter.MissingLocalNameOnly) conditions.Add("(p.name_local IS NULL OR trim(p.name_local)='')");
        if (filter.LooseOnly) conditions.Add("p.is_loose=1");
        return $"{string.Join(" AND ", conditions)} {expiry} {vendor} {stock}";
    }

    private (string Sql, DynamicParameters Parameters) MatchingSql(ProductSearchFilter filter, string query,
        string select, string tail)
    {
        var parameters = new DynamicParameters();
        AddFilterParameters(parameters, filter);
        string join = filter.LowStockOnly
            ? "JOIN product_stock_total stock ON stock.product_id=p.id AND stock.is_low=1" : string.Empty;
        string search = string.Empty;
        if (UsesFts(query))
        {
            join += " JOIN product_fts f ON f.rowid=p.id";
            search = "AND product_fts MATCH @match" + ShortTermConditions(query, parameters);
            parameters.Add("match", FtsMatch(query));
        }
        else if (query.Length > 0)
        {
            search = PrefixCondition;
            parameters.Add("prefix", PrefixPattern(query));
        }
        return ($"SELECT {select} FROM product p {join} WHERE {FilterConditions(filter)} {search} {tail}", parameters);
    }

    private long[] ExactMatches(System.Data.IDbConnection connection, string query, ProductSearchFilter filter)
    {
        var parameters = new DynamicParameters();
        AddFilterParameters(parameters, filter);
        parameters.Add("exact", query);
        return connection.Query<long>($"""
            SELECT p.id FROM product p
            WHERE p.id IN (SELECT id FROM product WHERE sku=@exact
                           UNION SELECT product_id FROM product_barcode WHERE barcode=@exact)
              AND {FilterConditions(filter)}
            ORDER BY p.name COLLATE NOCASE,p.id LIMIT 8
            """, parameters).ToArray();
    }

    // Whether a term matches enough products (200 or more) that walking names in order finds a
    // page sooner than sorting every match. Only an otherwise unfiltered search qualifies; the
    // answer is kept for five minutes per term.
    private bool CommonTerm(System.Data.IDbConnection connection, ProductSearchFilter filter, string match)
    {
        bool unfiltered = filter.CategoryId is null && filter.BrandId is null && !filter.LowStockOnly &&
            !filter.NoBarcodeOnly && !filter.MissingLocalNameOnly && !filter.LooseOnly && filter.ExpiryFilter == "ALL" &&
            filter.ProductClass is null && !filter.HasStockOnly && filter.VendorId is null && !filter.ArchivedOnly;
        if (!unfiltered) return false;
        lock (_searchPlanGate)
            if (_searchPlans.TryGetValue(match, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
                return cached.Common;
        bool common = connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM (SELECT rowid FROM product_fts WHERE product_fts MATCH @match LIMIT 200)
            """, new { match }) == 200;
        lock (_searchPlanGate)
        {
            if (_searchPlans.Count >= 32) _searchPlans.Clear();
            _searchPlans[match] = (DateTime.UtcNow.AddMinutes(5), common);
        }
        return common;
    }

    // The trigram index needs terms of three letters or more. A query with at least one uses the
    // index for those and filters its candidates by the shorter terms, so "salt 1 kg" is a lookup
    // rather than a substring scan of every product.
    private static bool UsesFts(string query) => Terms(query).Any(static term => term.Length >= 3);

    private static string[] Terms(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string FtsMatch(string query) => string.Join(' ', Terms(query).Where(static term => term.Length >= 3)
        .Select(static term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

    private static string ShortTermConditions(string query, DynamicParameters parameters)
    {
        var conditions = new System.Text.StringBuilder();
        int index = 0;
        foreach (string term in Terms(query).Where(static term => term.Length < 3).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string name = "short" + (index++).ToString(CultureInfo.InvariantCulture);
            parameters.Add(name, "%" + EscapeLike(term) + "%");
            conditions.Append(" AND (p.sku LIKE @").Append(name).Append(" ESCAPE '\\' OR p.name LIKE @").Append(name)
                .Append(" ESCAPE '\\' OR p.name_local LIKE @").Append(name).Append(" ESCAPE '\\' OR p.search_terms LIKE @")
                .Append(name).Append(" ESCAPE '\\')");
        }
        return conditions.ToString();
    }

    private const string PrefixCondition =
        "AND (p.sku LIKE @prefix ESCAPE '\\' OR p.name LIKE @prefix ESCAPE '\\' OR p.name_local LIKE @prefix ESCAPE '\\' OR p.search_terms LIKE @prefix ESCAPE '\\')";

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string PrefixPattern(string query)
    {
        string escaped = EscapeLike(query);
        return (query.Length >= 3 ? "%" : "") + escaped + "%";
    }

    // Projects a page of product IDs (selected by the inner query, which must return id and name)
    // to list rows with stock total and the FEFO lot's price, MRP and purchase price including tax.
    private static string ListSql(string visible) => $$"""
        WITH visible AS MATERIALIZED ({{visible}})
        SELECT p.id AS Id, p.sku AS Sku, p.name AS Name, p.name_local AS NameLocal,
               c.name AS Category, b.name AS Brand, u.code AS Uom,
               COALESCE(total.quantity,0) AS StockScaled,
               COALESCE(l.selling_price,p.default_selling_price) AS SellingPriceScaled,
               COALESCE(l.mrp,p.default_mrp) AS MrpScaled,
               l.unit_cost_incl_tax AS CostInclTaxScaled,
               p.is_loose AS IsLoose, p.is_active AS IsActive, p.product_class AS ProductClass,
               p.track_expiry AS TrackExpiry,
               EXISTS(SELECT 1 FROM product_barcode pb WHERE pb.product_id=p.id AND pb.lot_id IS NULL) AS HasBarcode,
               EXISTS(SELECT 1 FROM product_lot ex WHERE ex.product_id=p.id AND ex.qty_on_hand>0
                      AND ex.expiry_date<@expiryToday) AS HasExpiredStock
        FROM visible page JOIN product p ON p.id=page.id
        LEFT JOIN product_stock_total total ON total.product_id=p.id
        JOIN category c ON c.id=p.category_id
        JOIN uom u ON u.id=p.uom_id
        LEFT JOIN brand b ON b.id=p.brand_id
        LEFT JOIN product_lot l ON l.id=(
            SELECT pl.id FROM product_lot pl WHERE pl.product_id=p.id AND pl.qty_on_hand>0 AND pl.is_active=1
            ORDER BY CASE WHEN pl.expiry_date IS NULL THEN 1 ELSE 0 END, pl.expiry_date, pl.id LIMIT 1)
        ORDER BY page.name COLLATE NOCASE,page.id
        """;
}
