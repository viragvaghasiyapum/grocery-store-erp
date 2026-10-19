using System.Resources;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Catalog;

public sealed class CatalogModule(Type productsPage) : IModule
{
    public string Key => "Catalog";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation => [new("Catalog_Navigation", "Box24", productsPage, 30, "expiry")];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<CatalogService>();
        services.AddSingleton<ProductExcelImporter>();
        services.AddSingleton<ScanResolver>();
    }
}
