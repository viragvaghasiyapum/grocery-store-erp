using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public sealed partial class ShellViewModel(TranslationSource translations, DisplayFormatter formatter)
    : ObservableObject
{
    public string SampleAmount { get; } = $"₹{formatter.Number(12345678.9m)}";

    public string SampleDate { get; } = formatter.Date(new DateOnly(2026, 9, 22));

    [RelayCommand]
    private void UseEnglish() => translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");

    [RelayCommand]
    private void UseGujarati() => translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
}
