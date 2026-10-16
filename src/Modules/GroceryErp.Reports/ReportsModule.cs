using System.Resources;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Reports;

public sealed class ReportsModule(Type reportsPage) : IModule
{
    public string Key => "Reports";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation => [new("Reports_Navigation", "DataBarVertical24", reportsPage, 80)];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ReportService>();
        services.AddSingleton<ReportOutputService>();
    }
}
