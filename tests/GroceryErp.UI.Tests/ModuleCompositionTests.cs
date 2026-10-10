using GroceryErp.App.Presentation;
using GroceryErp.Assets;
using GroceryErp.Catalog;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Purchasing;
using GroceryErp.Reports;
using GroceryErp.Sales;
using GroceryErp.Settings;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

public sealed class ModuleCompositionTests
{
    [Fact]
    public void BusinessModules_RegisterServicesAndNavigation()
    {
        IModule[] modules =
        [
            new SalesModule(typeof(BillingPage), typeof(SalesHistoryPage)),
            new PurchasingModule(typeof(PurchasesPage)),
            new InventoryModule(typeof(StockPage)),
            new ExpensesModule(typeof(MoneyOutPage)),
            new AssetsModule(typeof(AssetsPage)),
            new ReportsModule(typeof(ReportsPage)),
            new CatalogModule(typeof(ProductsPage)),
            new SettingsModule(typeof(SettingsPage)),
        ];
        var services = new ServiceCollection();
        foreach (IModule module in modules) module.Register(services);

        modules.Select(static module => module.Key).Distinct().Count().ShouldBe(modules.Length);
        modules.SelectMany(static module => module.Navigation).Count().ShouldBe(9);
        modules.SelectMany(static module => module.Navigation).Select(static item => item.Order)
            .Distinct().Count().ShouldBe(9);
        services.ShouldContain(static descriptor => descriptor.ServiceType == typeof(SalesService));
        services.ShouldContain(static descriptor => descriptor.ServiceType == typeof(InventoryService));
        services.ShouldContain(static descriptor => descriptor.ServiceType == typeof(ReportService));
    }
}
