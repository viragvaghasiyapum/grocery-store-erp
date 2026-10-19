using System.Resources;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Inventory;

public sealed class InventoryModule(Type stockPage) : IModule
{
    public string Key => "Stock";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation => [new("Stock_Navigation", "Box24", stockPage, 50)];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<StockLedger>();
        services.AddSingleton<IStockLedger>(static provider => provider.GetRequiredService<StockLedger>());
        services.AddSingleton<InventoryService>();
        services.AddSingleton(static provider => new DirectStockCountService(
            provider.GetRequiredService<DbExecutor>(), new DirectStockEngine(),
            provider.GetRequiredService<PeriodGuard>()));
        services.AddSingleton<OpeningStockExcelImporter>();
    }
}
