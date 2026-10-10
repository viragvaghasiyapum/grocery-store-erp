using System.Resources;
using GroceryErp.Localization;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.Assets;

public sealed class AssetsModule(Type assetsPage) : IModule
{
    public string Key => "Assets";
    public ResourceManager Strings => SharedStrings.ResourceManager;
    public IEnumerable<NavItem> Navigation => [new("Assets_Navigation", "Building24", assetsPage, 60)];
    public void Register(IServiceCollection services) => services.AddSingleton<AssetService>();
}
