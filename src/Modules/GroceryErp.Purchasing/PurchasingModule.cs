using System.Resources;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Purchasing;

public sealed class PurchasingModule(Type purchasesPage, Type vendorsPage) : IModule
{
    public string Key => "Purchasing";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation =>
        [new("Purchasing_Navigation", "Cart24", purchasesPage, 30),
         new("Purchasing_Vendors", "People24", vendorsPage, 40)];
    public void Register(IServiceCollection services) => services.AddSingleton<PurchasingService>();
}
