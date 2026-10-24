using System.Resources;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Inventory;

// Stock work lives in Products (each product's Lots tab and bulk actions), so the module
// contributes services but no navigation destination of its own.
public sealed class InventoryModule : IModule
{
    public string Key => "Stock";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation => [];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<LotQuantities>();
        services.AddSingleton<ILotQuantities>(static provider => provider.GetRequiredService<LotQuantities>());
        services.AddSingleton<InventoryService>();
        services.AddSingleton(static provider => new DirectStockCountService(
            provider.GetRequiredService<DbExecutor>(), new DirectStockEngine(),
            provider.GetRequiredService<PeriodGuard>()));
        services.AddSingleton<OpeningStockExcelImporter>();
    }
}
