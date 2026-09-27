using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroceryErp.Data.Services;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public sealed partial class ShellViewModel(
    TranslationSource translations,
    DisplayFormatter formatter,
    SettingsService? settings = null)
    : ObservableObject
{
    public string SampleAmount { get; } = $"₹{formatter.Number(12345678.9m)}";

    public string SampleDate { get; } = formatter.Date(new DateOnly(2026, 9, 22));

    [RelayCommand]
    private Task UseEnglishAsync() => UseLanguageAsync("en-IN");

    [RelayCommand]
    private Task UseGujaratiAsync() => UseLanguageAsync("gu-IN");

    private async Task UseLanguageAsync(string language)
    {
        if (settings is not null && !string.Equals(settings.Current.UiLanguage, language, StringComparison.Ordinal))
            await settings.SaveAsync(new Dictionary<string, string> { ["ui.language"] = language });
        translations.UiCulture = CultureInfo.GetCultureInfo(language);
    }
}
