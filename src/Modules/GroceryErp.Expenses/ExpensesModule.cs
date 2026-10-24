using System.Resources;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Expenses;

public sealed class ExpensesModule(Type moneyOutPage) : IModule
{
    public string Key => "MoneyOut";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation => [new("MoneyOut_Navigation", "Money24", moneyOutPage, 60)];
    public void Register(IServiceCollection services) => services.AddSingleton<MoneyOutService>();
}
