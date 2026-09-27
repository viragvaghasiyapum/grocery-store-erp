using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Assets;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class AssetsPage : Page
{
    private readonly AssetService _assets;
    private readonly IClock _clock;
    private readonly PurchasingService _purchasing;

    public AssetsPage()
    {
        InitializeComponent();
        IServiceProvider services = ((App)Application.Current).Services;
        _assets = services.GetRequiredService<AssetService>();
        _clock = services.GetRequiredService<IClock>();
        _purchasing = services.GetRequiredService<PurchasingService>();
        AssetRate.Text = "10";
        DateTime today = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        PurchaseDate.SelectedDate = today;
        InUseDate.SelectedDate = today;
        DisposalDate.SelectedDate = today;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void AddAsset_OnClick(object sender, RoutedEventArgs e)
    {
        if (PurchaseDate.SelectedDate is not { } purchase || InUseDate.SelectedDate is not { } inUse) return;
        try
        {
            string category = ((ComboBoxItem)AssetCategory.SelectedItem).Content.ToString() ?? "Other";
            string method = ((ComboBoxItem)AssetMethod.SelectedItem).Content.ToString() ?? "WDV";
            await _assets.SaveAssetAsync(null, new(AssetName.Text, category, DateOnly.FromDateTime(purchase), DateOnly.FromDateTime(inUse),
                new Money(Parse(AssetCost.Text)), AssetVendor.SelectedValue is long vendorId ? vendorId : null,
                method == "WDV" ? DepreciationMethod.Wdv : DepreciationMethod.Slm,
                new Percent(Parse(AssetRate.Text)), new Money(Parse(AssetSalvage.Text)), AssetNotes.Text));
            AssetName.Clear(); AssetCost.Clear(); AssetNotes.Clear(); await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void AssetCategory_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || AssetCategory.SelectedItem is not ComboBoxItem item) return;
        AssetRate.Text = item.Content?.ToString() switch
        {
            "Furniture" => "10",
            "Electronics" => "40",
            "Vehicle" => "15",
            _ => "15",
        };
    }

    private async void Dispose_OnClick(object sender, RoutedEventArgs e)
    {
        if (AssetGrid.SelectedItem is not AssetItem asset || DisposalDate.SelectedDate is not { } date) return;
        try { await _assets.DisposeAsync(asset.Id, DateOnly.FromDateTime(date), new Money(Parse(DisposalValue.Text)), OperationGuard.NewId()); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DepreciationPostResult result = await _assets.PostDueDepreciationAsync();
            StatusText.Text = $"{result.PostedCount} posted; {result.LockedPeriods.Count} locked";
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void AssetGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssetGrid.SelectedItem is not AssetItem asset) return;
        try { ScheduleGrid.ItemsSource = await _assets.GetScheduleAsync(asset.Id); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async Task RefreshAsync()
    {
        try
        {
            AssetGrid.ItemsSource = await _assets.GetAssetsAsync();
            AssetVendor.ItemsSource = await _purchasing.GetVendorsAsync();
            StatusText.Text = "OK";
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private static decimal Parse(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"), out decimal value))
            throw new FormatException("Enter a valid number.");
        return value;
    }
}
