using System.Resources;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Settings;

public sealed class SettingsModule(Type settingsPage) : IModule
{
    public string Key => "Settings";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation => [new("Settings_Title", "Settings24", settingsPage, 90)];
    public void Register(IServiceCollection services) { }
}
