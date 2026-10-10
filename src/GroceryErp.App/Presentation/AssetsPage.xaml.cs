using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Assets;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class AssetsPage : Page
{
    private readonly AssetService _assets;
    private readonly IClock _clock;
    private readonly PurchasingService _purchasing;
    private AssetItem? _editingAsset;

    public AssetsPage() : this(null, null, null) { }

    public AssetsPage(AssetService? assets, IClock? clock, PurchasingService? purchasing, bool loadOnShow = true)
    {
        InitializeComponent();
        IServiceProvider? services = assets is null || clock is null || purchasing is null
            ? ((App)Application.Current).Services : null;
        _assets = assets ?? services!.GetRequiredService<AssetService>();
        _clock = clock ?? services!.GetRequiredService<IClock>();
        _purchasing = purchasing ?? services!.GetRequiredService<PurchasingService>();
        AssetRate.Text = "10";
        DateTime today = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        PurchaseDate.SelectedDate = today;
        InUseDate.SelectedDate = today;
        DisposalDate.SelectedDate = today;
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private async void AddAsset_OnClick(object sender, RoutedEventArgs e)
    {
        if (PurchaseDate.SelectedDate is not { } purchase || InUseDate.SelectedDate is not { } inUse) return;
        try
        {
            string category = ((ComboBoxItem)AssetCategory.SelectedItem).Tag?.ToString() ?? "Other";
            string method = ((ComboBoxItem)AssetMethod.SelectedItem).Tag?.ToString() ?? "WDV";
            await _assets.SaveAssetAsync(_editingAsset?.Id, new(AssetName.Text, category, DateOnly.FromDateTime(purchase), DateOnly.FromDateTime(inUse),
                new Money(Parse(AssetCost.Text)), AssetVendor.SelectedValue is long vendorId ? vendorId : null,
                method == "WDV" ? DepreciationMethod.Wdv : DepreciationMethod.Slm,
                new Percent(Parse(AssetRate.Text)), new Money(Parse(AssetSalvage.Text)), AssetNotes.Text));
            NewAsset_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void AssetCategory_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || AssetCategory.SelectedItem is not ComboBoxItem item) return;
        AssetRate.Text = item.Tag?.ToString() switch
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
        if (MessageBox.Show(TranslationSource.Instance.Format("Assets_DisposeConfirm", asset.Name),
            TranslationSource.Instance["Assets_Dispose"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _assets.DisposeAsync(asset.Id, DateOnly.FromDateTime(date), new Money(Parse(DisposalValue.Text)), OperationGuard.NewId()); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DepreciationPostResult result = await _assets.PostDueDepreciationAsync();
            await RefreshAsync();
            StatusText.Text = TranslationSource.Instance.Format("Assets_PostResult", result.PostedCount, result.LockedPeriods.Count);
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void AssetGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssetGrid.SelectedItem is not AssetItem asset) return;
        try
        {
            _editingAsset = asset;
            AssetName.Text = asset.Name;
            AssetCategory.SelectedItem = AssetCategory.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == asset.Category);
            PurchaseDate.SelectedDate = asset.PurchaseDate.ToDateTime(TimeOnly.MinValue);
            InUseDate.SelectedDate = asset.InUseFrom.ToDateTime(TimeOnly.MinValue);
            AssetCost.Text = asset.Cost.Value.ToString("0.000", TranslationSource.FormatCulture);
            AssetMethod.SelectedIndex = asset.Method == DepreciationMethod.Wdv ? 0 : 1;
            AssetRate.Text = asset.Rate.Value.ToString("0.000", TranslationSource.FormatCulture);
            AssetSalvage.Text = asset.SalvageValue.Value.ToString("0.000", TranslationSource.FormatCulture);
            AssetVendor.SelectedValue = asset.VendorId;
            AssetNotes.Text = asset.Notes ?? "";
            IReadOnlyList<DepreciationEntry> schedule = await _assets.GetScheduleAsync(asset.Id);
            ScheduleGrid.ItemsSource = schedule;
            SaveAssetButton.IsEnabled = asset.DisposedDate is null && schedule.Count == 0;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void NewAsset_OnClick(object sender, RoutedEventArgs e)
    {
        AssetGrid.SelectedItem = null;
        _editingAsset = null;
        AssetName.Clear(); AssetCost.Clear(); AssetNotes.Clear();
        AssetCategory.SelectedIndex = 0; AssetMethod.SelectedIndex = 0;
        AssetRate.Text = "10"; AssetSalvage.Text = "0";
        AssetVendor.SelectedIndex = -1;
        PurchaseDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        InUseDate.SelectedDate = PurchaseDate.SelectedDate;
        SaveAssetButton.IsEnabled = true;
        ScheduleGrid.ItemsSource = null;
        AssetName.Focus();
    }

    private async Task RefreshAsync()
    {
        try
        {
            long? selectedId = _editingAsset?.Id;
            IReadOnlyList<AssetItem> assets = await _assets.GetAssetsAsync();
            AssetVendor.ItemsSource = await _purchasing.GetVendorsAsync();
            AssetGrid.ItemsSource = assets;
            AssetGrid.SelectedItem = assets.FirstOrDefault(asset => asset.Id == selectedId);
            StatusText.Text = TranslationSource.Instance["MoneyOut_Ready"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private static decimal Parse(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"), out decimal value))
            throw new FormatException(TranslationSource.Instance["MoneyOut_ValidNumber"]);
        return value;
    }
}
