using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GroceryErp.Localization;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace GroceryErp.App.Presentation;

/// <summary>
/// The ⓘ beside a screen's title. Its tooltip says what the screen is for; a click opens the
/// screen's help, as F1 does from any field. It takes the topic its screen's root names.
/// </summary>
public sealed class HelpButton : Button
{
    public HelpButton()
    {
        Content = new SymbolIcon { Symbol = SymbolRegular.Info20, FontSize = 20 };
        Margin = new Thickness(0, 0, 16, 0);
        SetResourceReference(StyleProperty, "HelpButton");
        SetBinding(AutomationProperties.NameProperty,
            new Binding("[Help_Button]") { Source = TranslationSource.Instance, Mode = BindingMode.OneWay });
        // Built when it opens, so it is always in the language on screen at that moment.
        ToolTip = string.Empty;
        ToolTipOpening += (_, _) => ToolTip = Tip();
        Click += (_, _) =>
        {
            if (ScreenHelp.GetTopic(this) is { Length: > 0 } topic) ScreenHelp.Open(this, topic, field: null);
        };
    }

    private TextBlock Tip()
    {
        string hint = TranslationSource.Instance["Help_Hint"];
        string? topic = ScreenHelp.GetTopic(this);
        string? summary = topic is { Length: > 0 }
            ? ScreenHelp.Read(topic, TranslationSource.Instance.UiCulture)?.Summary
            : null;
        var tip = new TextBlock
        {
            MaxWidth = 380,
            TextWrapping = TextWrapping.Wrap,
            Text = summary is null ? hint : summary + "\n\n" + hint,
        };
        // A tooltip draws in its own window, so it pins English digits itself.
        NumberSubstitution.SetCultureSource(tip, NumberCultureSource.Override);
        NumberSubstitution.SetCultureOverride(tip, TranslationSource.FormatCulture);
        NumberSubstitution.SetSubstitution(tip, NumberSubstitutionMethod.European);
        return tip;
    }
}
