using System.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.UI.Common.Modularity;

/// <summary>Defines a business module's registrations, resources, and shell navigation.</summary>
public interface IModule
{
    /// <summary>Gets the stable module key and resource prefix.</summary>
    string Key { get; }

    /// <summary>Gets the module's localized string manager.</summary>
    ResourceManager Strings { get; }

    /// <summary>Registers the module at the composition edge.</summary>
    void Register(IServiceCollection services);

    /// <summary>Gets the module's shell navigation items.</summary>
    IEnumerable<NavItem> Navigation { get; }
}

