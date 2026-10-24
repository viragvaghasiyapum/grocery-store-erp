using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GroceryErp.Assets;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Localization;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Adds or edits an asset. For an edited asset, changed depreciation terms show before saving
/// which months recalculate and the depreciation and book-value effect; blocked changes cannot save.
/// </summary>
public partial class AssetFormWindow : Window
{
    private readonly AssetService _assets;
    private readonly AssetItem? _asset;
    private readonly LatestRequest _preview = new();
    private readonly DispatcherTimer _previewDelay;
    private bool _setting;

    public AssetFormWindow(AssetService assets, IClock clock, IReadOnlyList<VendorItem> vendors, AssetItem? asset = null)
    {
        InitializeComponent();
        _assets = assets;
        _asset = asset;
        AssetVendor.ItemsSource = vendors;
        _previewDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _previewDelay.Tick += async (_, _) =>
        {
            _previewDelay.Stop();
            await PreviewAsync();
        };
        _setting = true;
        DateTime today = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        PurchaseDate.SelectedDate = today;
        InUseDate.SelectedDate = today;
        if (asset is not null)
        {
            Title = TranslationSource.Instance["Assets_EditSelected"];
            TitleText.Text = asset.Name;
            AssetName.Text = asset.Name;
            AssetCategory.SelectedItem = AssetCategory.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == asset.Category);
            PurchaseDate.SelectedDate = asset.PurchaseDate.ToDateTime(TimeOnly.MinValue);
            InUseDate.SelectedDate = asset.InUseFrom.ToDateTime(TimeOnly.MinValue);
            AssetCost.Text = asset.Cost.Value.ToString("0.###", TranslationSource.FormatCulture);
            AssetMethod.SelectedIndex = asset.Method == DepreciationMethod.Wdv ? 0 : 1;
            AssetRate.Text = asset.Rate.Value.ToString("0.###", TranslationSource.FormatCulture);
            AssetSalvage.Text = asset.SalvageValue.Value.ToString("0.###", TranslationSource.FormatCulture);
            AssetVendor.SelectedValue = asset.VendorId;
            AssetNotes.Text = asset.Notes ?? "";
            // A disposed asset keeps its final terms.
            if (asset.DisposedDate is not null)
            {
                foreach (FormField field in new[] { PurchaseField, InUseField, CostField, MethodField, RateField, SalvageField })
                    field.IsEnabled = false;
                ShowNote(TranslationSource.Instance["Assets_FinancialLocked"]);
            }
        }
        _setting = false;
        Loaded += (_, _) => NameField.FocusInput();
    }

    private void ShowNote(string text)
    {
        AssetFinancialLockText.Text = text;
        AssetFinancialLockText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AssetCategory_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _setting || _asset is not null || AssetCategory.SelectedItem is not ComboBoxItem item) return;
        AssetRate.Text = item.Tag?.ToString() switch
        {
            "Furniture" => "10",
            "Electronics" => "40",
            _ => "15",
        };
    }

    private void AssetTerms_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _asset is null || _setting) return;
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private (AssetInput? Input, YearMonth? Effective, FormValidator Form) Read()
    {
        var form = new FormValidator();
        string? name = form.Text(NameField, AssetName.Text, required: true, maxLength: 120);
        DateOnly? purchase = form.Date(PurchaseField, PurchaseDate.SelectedDate);
        DateOnly? inUse = form.Date(InUseField, InUseDate.SelectedDate);
        if (inUse < purchase) form.AddKey(InUseField, "Assets_InUseBeforePurchase");
        Money? cost = form.Money(CostField, AssetCost.Text, required: true, positive: true);
        decimal? rate = form.Number(RateField, AssetRate.Text, required: true, minimum: 0);
        Money? salvage = form.Money(SalvageField, AssetSalvage.Text, required: true);
        YearMonth? effective = null;
        if (EffectiveField.Visibility == Visibility.Visible)
        {
            if (YearMonth.TryParse(InputNormalizer.Normalize(AssetEffectiveFrom.Text).Trim(), out YearMonth month)) effective = month;
            else form.AddKey(EffectiveField, "MoneyOut_SalaryFromFormat");
        }
        if (!form.IsValid) return (null, effective, form);
        string category = (AssetCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Other";
        string method = (AssetMethod.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "WDV";
        return (new AssetInput(name!, category, purchase!.Value, inUse!.Value, cost!.Value,
            AssetVendor.SelectedValue is long vendorId ? vendorId : null,
            method == "WDV" ? DepreciationMethod.Wdv : DepreciationMethod.Slm,
            new Percent(rate!.Value), salvage!.Value, AssetNotes.Text), effective, form);
    }

    // Shows, before saving, which months a financial change recalculates and its effect.
    private async Task PreviewAsync()
    {
        if (_asset is null) return;
        (AssetInput? input, YearMonth? effective, _) = Read();
        if (input is null)
        {
            ShowNote(TranslationSource.Instance["Assets_PreviewNeedsValues"]);
            return;
        }
        using LatestRequest.Scope request = _preview.Begin();
        try
        {
            AssetChangePreview preview = await _assets.PreviewChangeAsync(_asset.Id, input, effective, request.Token);
            if (!request.IsCurrent) return;
            bool terms = preview.Treatment == "TERMS";
            if (terms && EffectiveField.Visibility != Visibility.Visible)
            {
                EffectiveField.Visibility = Visibility.Visible;
                _setting = true;
                AssetEffectiveFrom.Text = preview.EffectiveMonth?.ToString() ?? "";
                _setting = false;
            }
            else if (!terms && preview.Treatment != "BLOCKED") EffectiveField.Visibility = Visibility.Collapsed;
            ShowNote(preview.Treatment == "DETAILS" ? "" : DescribePreview(preview));
            SaveAssetButton.IsEnabled = preview.Treatment != "BLOCKED";
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) ShowNote(UserError.Message(exception));
        }
    }

    internal static string DescribePreview(AssetChangePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        string Money(Money value) => value.Value.ToString("N3", TranslationSource.FormatCulture);
        return preview.Treatment switch
        {
            "BLOCKED" => TranslationSource.Instance.Format("Assets_PreviewBlocked", preview.FirstOpenMonth.ToString()),
            "RECALCULATION" => TranslationSource.Instance.Format("Assets_PreviewRecalculation",
                preview.EffectiveMonth?.ToString() ?? "", preview.MonthsRecalculated, Money(preview.DepreciationBefore),
                Money(preview.DepreciationAfter), Money(preview.BookValueBefore), Money(preview.BookValueAfter)),
            _ => TranslationSource.Instance.Format("Assets_PreviewTerms", preview.EffectiveMonth?.ToString() ?? "",
                preview.FirstOpenMonth.ToString(), preview.MonthsRecalculated, Money(preview.DepreciationBefore),
                Money(preview.DepreciationAfter), Money(preview.BookValueBefore), Money(preview.BookValueAfter)),
        };
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        (AssetInput? input, YearMonth? effective, FormValidator form) = Read();
        if (!form.Apply(Fields, Summary) || input is null) return;
        SaveAssetButton.IsEnabled = false;
        try
        {
            if (_asset is not null)
            {
                AssetChangePreview preview = await _assets.PreviewChangeAsync(_asset.Id, input, effective);
                if (preview.Treatment == "BLOCKED")
                {
                    Summary.ShowMessage(DescribePreview(preview));
                    return;
                }
                if (preview.Treatment != "DETAILS" && MessageBox.Show(DescribePreview(preview),
                        TranslationSource.Instance["Assets_ConfirmChange"], MessageBoxButton.OKCancel,
                        MessageBoxImage.Information) != MessageBoxResult.OK) return;
            }
            await _assets.SaveAssetAsync(_asset?.Id, input, effective);
            DialogResult = true;
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
        finally
        {
            if (IsLoaded) SaveAssetButton.IsEnabled = true;
        }
    }
}
