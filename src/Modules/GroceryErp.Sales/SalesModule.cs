using System.Resources;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Sales;

public sealed class SalesModule(Type billingPage, Type historyPage) : IModule
{
    public string Key => "Sales";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation =>
        [new("Sales_Pos_Navigation", "Cart24", billingPage, 10),
         new("Sales_History_Navigation", "Document24", historyPage, 20)];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<SalesService>();
        services.AddSingleton<ReceiptPrinter>();
        services.AddSingleton<PrintJobJournal>();
    }
}
