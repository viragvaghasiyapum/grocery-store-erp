using System.Windows.Data;
using System.Windows.Markup;

namespace GroceryErp.Localization;

/// <summary>Creates a one-way binding to a runtime-switchable resource key.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    /// <summary>Initializes an empty markup extension for XAML tooling.</summary>
    public TrExtension()
    {
    }

    /// <summary>Initializes a markup extension for the specified resource key.</summary>
    public TrExtension(string key)
    {
        Key = key;
    }

    /// <summary>Gets or sets the resource key.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = TranslationSource.Instance,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}

