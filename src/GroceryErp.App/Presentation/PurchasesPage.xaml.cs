using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class PurchasesPage : Page
{
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
    private readonly PurchasingService _purchasing;
    private readonly Stack<(DateOnly? Date, long? Id)> _previousPages = new();
    private DateOnly? _beforeDate;
    private long? _beforeId;
    private PurchasePage? _currentPage;
    private bool _loadingPage;
    private readonly Dictionary<long, PurchaseListItem> _selectedPurchases = new();
    private CheckBox? _pageSelectionCheck;
    private bool _checkboxSelectionUsed;
    private readonly Dictionary<long, VendorItem> _selectedVendors = new();
    private CheckBox? _vendorPageSelectionCheck;
    private bool _vendorCheckboxSelectionUsed;

    public PurchasesPage() : this(null) { }

    public PurchasesPage(PurchasingService? purchasing, bool loadOnShow = true)
    {
        InitializeComponent();
        _purchasing = purchasing ?? ((App)Application.Current).Services.GetRequiredService<PurchasingService>();
        UpdatePurchaseSelectionActions();
        UpdateVendorSelectionActions();
        Loaded += (_, _) => TranslationSource.Instance.PropertyChanged += TranslationChanged;
        Unloaded += (_, _) => TranslationSource.Instance.PropertyChanged -= TranslationChanged;
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private void TranslationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]" && IsLoaded)
        {
            UpdatePurchaseSelectionActions();
            UpdateVendorSelectionActions();
        }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        if (PurchasingTabs.SelectedIndex == 1)
        {
            await RefreshVendorsSafelyAsync();
        }
        else await RefreshAsync();
    }

    private async void FilterPurchases_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ClearPurchaseFilters_OnClick(object sender, RoutedEventArgs e)
    {
        PurchaseFromBox.SelectedDate = null;
        PurchaseToBox.SelectedDate = null;
        PurchaseVendorFilter.SelectedValue = null;
        PurchaseStatusFilter.SelectedIndex = 0;
        PurchaseTypeFilter.SelectedIndex = 0;
        PurchaseProductFilter.Clear();
        await RefreshAsync();
    }

    private async void NewVendor_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new VendorEditorWindow(_purchasing) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshVendorsSafelyAsync();
    }

    private async void NewPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new PurchaseEntryWindow(_purchasing) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        await RefreshAsync();
    }

    private async void OpenDraft_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSinglePurchase() is not { Status: "DRAFT" } purchase) return;
        var window = new PurchaseEntryWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        await RefreshAsync();
    }

    private async void ViewInvoice_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSinglePurchase() is not { Status: "POSTED" or "CANCELLED" } purchase) return;
        try
        {
            PurchaseInvoice invoice = await _purchasing.GetInvoiceAsync(purchase.Id);
            new PurchaseInvoiceWindow(invoice) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void PurchasesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedPurchases.Count == 0 && e.AddedItems.Count > 0) _checkboxSelectionUsed = false;
        UpdatePurchaseSelectionActions();
    }

    private void PurchaseCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: PurchaseListItem purchase } box)
            box.IsChecked = _selectedPurchases.ContainsKey(purchase.Id);
    }

    private void PurchaseCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: PurchaseListItem purchase } box) return;
        _checkboxSelectionUsed = true;
        if (box.IsChecked == true) _selectedPurchases[purchase.Id] = purchase;
        else _selectedPurchases.Remove(purchase.Id);
        UpdatePurchaseSelectionActions();
    }

    private void PurchasePageCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        _pageSelectionCheck = sender as CheckBox;
        UpdatePageSelectionCheck();
    }

    private void PurchasePageCheck_OnClick(object sender, RoutedEventArgs e)
    {
        PurchaseListItem[] pageItems = PurchasesGrid.Items.OfType<PurchaseListItem>().ToArray();
        if (pageItems.Length == 0) return;
        bool allSelected = pageItems.All(item => _selectedPurchases.ContainsKey(item.Id));
        _checkboxSelectionUsed = true;
        foreach (PurchaseListItem item in pageItems)
        {
            if (allSelected) _selectedPurchases.Remove(item.Id);
            else _selectedPurchases[item.Id] = item;
        }
        if (allSelected) PurchasesGrid.UnselectAll();
        PurchasesGrid.Items.Refresh();
        UpdatePurchaseSelectionActions();
    }

    private PurchaseListItem? SelectedSinglePurchase() => _selectedPurchases.Count switch
    {
        > 1 => null,
        1 => _selectedPurchases.Values.Single(),
        _ => _checkboxSelectionUsed ? null : PurchasesGrid.SelectedItem as PurchaseListItem,
    };

    private void UpdatePageSelectionCheck()
    {
        if (_pageSelectionCheck is null) return;
        PurchaseListItem[] pageItems = PurchasesGrid.Items.OfType<PurchaseListItem>().ToArray();
        int selected = pageItems.Count(item => _selectedPurchases.ContainsKey(item.Id));
        _pageSelectionCheck.IsChecked = selected == 0 ? false : selected == pageItems.Length ? true : null;
    }

    private void UpdatePurchaseSelectionActions()
    {
        if (OpenDraftButton is null) return;
        PurchaseListItem? purchase = SelectedSinglePurchase();
        OpenDraftButton.IsEnabled = purchase is { Status: "DRAFT" };
        ViewInvoiceButton.IsEnabled = purchase is { Status: "POSTED" or "CANCELLED" };
        CancelPurchaseButton.IsEnabled = purchase is { Status: "POSTED" };
        VendorReturnButton.IsEnabled = purchase is { Status: "POSTED" };
        ReturnHistoryButton.IsEnabled = purchase is { Status: "POSTED" or "CANCELLED" };
        bool multiple = _selectedPurchases.Count > 1;
        BulkCancelPurchaseButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        BulkCancelPurchaseButton.IsEnabled = multiple && _selectedPurchases.Values.All(static item => item.Status == "POSTED");
        int count = _selectedPurchases.Count > 0 || _checkboxSelectionUsed
            ? _selectedPurchases.Count : purchase is null ? 0 : 1;
        PurchaseSelectedCountText.Text = TranslationSource.Instance.Format("Selection_Count", count);
        UpdatePageSelectionCheck();
    }

    private void ClearPurchaseSelection()
    {
        _selectedPurchases.Clear();
        _checkboxSelectionUsed = false;
        PurchasesGrid.UnselectAll();
        PurchasesGrid.Items.Refresh();
        UpdatePurchaseSelectionActions();
    }

    private void VendorsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedVendors.Count == 0 && e.AddedItems.Count > 0) _vendorCheckboxSelectionUsed = false;
        UpdateVendorSelectionActions();
    }

    private void VendorCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: VendorItem vendor } box)
            box.IsChecked = _selectedVendors.ContainsKey(vendor.Id);
    }

    private void VendorCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: VendorItem vendor } box) return;
        _vendorCheckboxSelectionUsed = true;
        if (box.IsChecked == true) _selectedVendors[vendor.Id] = vendor;
        else _selectedVendors.Remove(vendor.Id);
        UpdateVendorSelectionActions();
    }

    private void VendorPageCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        _vendorPageSelectionCheck = sender as CheckBox;
        UpdateVendorPageSelectionCheck();
    }

    private void VendorPageCheck_OnClick(object sender, RoutedEventArgs e)
    {
        VendorItem[] vendors = VendorsGrid.Items.OfType<VendorItem>().ToArray();
        if (vendors.Length == 0) return;
        bool allSelected = vendors.All(item => _selectedVendors.ContainsKey(item.Id));
        _vendorCheckboxSelectionUsed = true;
        foreach (VendorItem vendor in vendors)
        {
            if (allSelected) _selectedVendors.Remove(vendor.Id);
            else _selectedVendors[vendor.Id] = vendor;
        }
        if (allSelected) VendorsGrid.UnselectAll();
        VendorsGrid.Items.Refresh();
        UpdateVendorSelectionActions();
    }

    private VendorItem? SelectedSingleVendor() => _selectedVendors.Count switch
    {
        > 1 => null,
        1 => _selectedVendors.Values.Single(),
        _ => _vendorCheckboxSelectionUsed ? null : VendorsGrid.SelectedItem as VendorItem,
    };

    private void UpdateVendorPageSelectionCheck()
    {
        if (_vendorPageSelectionCheck is null) return;
        VendorItem[] vendors = VendorsGrid.Items.OfType<VendorItem>().ToArray();
        int selected = vendors.Count(item => _selectedVendors.ContainsKey(item.Id));
        _vendorPageSelectionCheck.IsChecked = selected == 0 ? false : selected == vendors.Length ? true : null;
    }

    private void UpdateVendorSelectionActions()
    {
        if (EditVendorButton is null) return;
        VendorItem? vendor = SelectedSingleVendor();
        EditVendorButton.IsEnabled = vendor is not null;
        VendorLedgerButton.IsEnabled = vendor is not null;
        bool checkedVendors = _selectedVendors.Count > 0;
        ActivateVendorsButton.Visibility = checkedVendors ? Visibility.Visible : Visibility.Collapsed;
        DeactivateVendorsButton.Visibility = checkedVendors ? Visibility.Visible : Visibility.Collapsed;
        ActivateVendorsButton.IsEnabled = _selectedVendors.Values.Any(static item => !item.IsActive);
        DeactivateVendorsButton.IsEnabled = _selectedVendors.Values.Any(static item => item.IsActive);
        int count = checkedVendors || _vendorCheckboxSelectionUsed
            ? _selectedVendors.Count : vendor is null ? 0 : 1;
        VendorSelectedCountText.Text = TranslationSource.Instance.Format("Selection_Count", count);
        UpdateVendorPageSelectionCheck();
    }

    private void ClearVendorSelection()
    {
        _selectedVendors.Clear();
        _vendorCheckboxSelectionUsed = false;
        VendorsGrid.UnselectAll();
        VendorsGrid.Items.Refresh();
        UpdateVendorSelectionActions();
    }

    private async void EditVendor_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSingleVendor() is not VendorItem vendor) return;
        var window = new VendorEditorWindow(_purchasing, vendor.Id) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshVendorsSafelyAsync();
    }

    private void VendorLedger_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSingleVendor() is not VendorItem vendor) return;
        new VendorLedgerWindow(_purchasing, vendor.Id, vendor.Name) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void ActivateVendors_OnClick(object sender, RoutedEventArgs e) => await ChangeVendorsActiveAsync(true);

    private async void DeactivateVendors_OnClick(object sender, RoutedEventArgs e) => await ChangeVendorsActiveAsync(false);

    private async Task ChangeVendorsActiveAsync(bool active)
    {
        VendorItem[] vendors = _selectedVendors.Values.ToArray();
        if (vendors.Length == 0 || vendors.All(item => item.IsActive == active)) return;
        if (!active && MessageBox.Show(TranslationSource.Instance.Format("Purchasing_DeactivateVendorsConfirm", vendors.Length),
            TranslationSource.Instance["Purchasing_DeactivateSelectedVendors"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ActivateVendorsButton.IsEnabled = false;
        DeactivateVendorsButton.IsEnabled = false;
        try
        {
            int changed = await _purchasing.BulkSetVendorsActiveAsync(vendors.Select(static item => item.Id).ToArray(), active);
            await RefreshVendorsAsync();
            StatusText.Text = TranslationSource.Instance.Format("Purchasing_VendorStatusChanged", changed);
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
            UpdateVendorSelectionActions();
        }
    }

    private async void Payment_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new VendorPaymentWindow(_purchasing) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshVendorsSafelyAsync();
    }

    private async void CancelPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSinglePurchase() is not { Status: "POSTED" } purchase) return;
        if (MessageBox.Show(TranslationSource.Instance["Purchasing_CancelConfirm"],
            TranslationSource.Instance["Purchasing_CancelPurchase"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            IServiceProvider services = ((App)Application.Current).Services;
            await _purchasing.CancelAsync(purchase.Id, services.GetRequiredService<IClock>().BusinessDate, OperationGuard.NewId());
            await RefreshAsync();
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.CancelNotAllowed)
        {
            StatusText.Text = TranslationSource.Instance["Purchasing_CancelBlocked"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void BulkCancelPurchases_OnClick(object sender, RoutedEventArgs e)
    {
        PurchaseListItem[] purchases = _selectedPurchases.Values.ToArray();
        if (purchases.Length < 2 || purchases.Any(static item => item.Status != "POSTED")) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("Purchasing_BulkCancelConfirm", purchases.Length),
            TranslationSource.Instance["Purchasing_CancelSelected"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        BulkCancelPurchaseButton.IsEnabled = false;
        int cancelled = 0;
        var failures = new List<string>();
        DateOnly date = ((App)Application.Current).Services.GetRequiredService<IClock>().BusinessDate;
        foreach (PurchaseListItem purchase in purchases)
        {
            try
            {
                await _purchasing.CancelAsync(purchase.Id, date, OperationGuard.NewId());
                cancelled++;
            }
            catch (Exception exception)
            {
                failures.Add($"{purchase.DocumentNumber ?? purchase.VendorInvoiceNumber ?? purchase.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {UserError.Message(exception)}");
            }
        }
        await RefreshAsync();
        StatusText.Text = TranslationSource.Instance.Format("Purchasing_BulkCancelResult", cancelled, failures.Count);
        if (failures.Count > 0)
            MessageBox.Show(string.Join(Environment.NewLine, failures),
                TranslationSource.Instance["Purchasing_CancelSelected"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void VendorReturn_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSinglePurchase() is not { Status: "POSTED" } purchase) return;
        var window = new VendorReturnWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        await RefreshAsync();
        StatusText.Text = TranslationSource.Instance.Format("Purchasing_ReturnPosted", window.DocumentNumber ?? "");
    }

    private async void ReturnHistory_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSinglePurchase() is not { Status: "POSTED" or "CANCELLED" } purchase) return;
        new VendorReturnHistoryWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) }.ShowDialog();
        await RefreshAsync();
    }

    private Task RefreshAsync() => LoadPageAsync(null, null, reset: true);

    private async Task LoadPageAsync(DateOnly? beforeDate, long? beforeId, bool reset)
    {
        using var timing = UiRefreshProbe.Track("purchases", this);
        if (_loadingPage) return;
        _loadingPage = true;
        PreviousPurchaseButton.IsEnabled = false;
        NextPurchaseButton.IsEnabled = false;
        try
        {
            PurchasePage page = await _purchasing.GetPurchasesPageAsync(beforeDate, beforeId,
                PurchaseFromBox.SelectedDate is { } from ? DateOnly.FromDateTime(from) : null,
                PurchaseToBox.SelectedDate is { } to ? DateOnly.FromDateTime(to) : null,
                PurchaseVendorFilter.SelectedValue as long?, PageSize, PurchaseProductFilter.Text,
                (PurchaseStatusFilter.SelectedValue as string) is { Length: > 0 } status ? status : null,
                (PurchaseTypeFilter.SelectedValue as string) is { Length: > 0 } kind ? kind : null);
            if (reset)
            {
                _previousPages.Clear();
                ClearPurchaseSelection();
            }
            _beforeDate = beforeDate;
            _beforeId = beforeId;
            _currentPage = page;
            timing.Bind(() => PurchasesGrid.ItemsSource = page.Items);
            UpdatePurchaseSelectionActions();
            if (reset)
            {
                await RefreshVendorsAsync();
            }
            StatusText.Text = $"{PurchasesGrid.Items.Count} / {VendorsGrid.Items.Count}";
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _loadingPage = false;
            PreviousPurchaseButton.IsEnabled = _previousPages.Count > 0;
            NextPurchaseButton.IsEnabled = _currentPage?.HasMore == true;
        }
    }

    private async Task RefreshVendorsAsync()
    {
        long? selectedVendor = PurchaseVendorFilter.SelectedValue as long?;
        IReadOnlyList<VendorItem> vendors = await _purchasing.GetVendorsAsync(includeInactive: true);
        ClearVendorSelection();
        VendorsGrid.ItemsSource = vendors;
        UpdateVendorSelectionActions();
        PurchaseVendorFilter.ItemsSource = vendors;
        PurchaseVendorFilter.SelectedValue = selectedVendor;
        StatusText.Text = $"{PurchasesGrid.Items.Count} / {VendorsGrid.Items.Count}";
    }

    private async Task RefreshVendorsSafelyAsync()
    {
        try { await RefreshVendorsAsync(); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void NextPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingPage || _currentPage is not { HasMore: true, NextDate: not null, NextId: not null } page) return;
        DateOnly? oldDate = _beforeDate;
        long? oldId = _beforeId;
        await LoadPageAsync(page.NextDate, page.NextId, reset: false);
        if (_beforeDate == page.NextDate && _beforeId == page.NextId)
            _previousPages.Push((oldDate, oldId));
        PreviousPurchaseButton.IsEnabled = _previousPages.Count > 0;
    }

    private async void PreviousPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingPage || _previousPages.Count == 0) return;
        (DateOnly? date, long? id) = _previousPages.Peek();
        await LoadPageAsync(date, id, reset: false);
        if (_beforeDate == date && _beforeId == id) _previousPages.Pop();
        PreviousPurchaseButton.IsEnabled = _previousPages.Count > 0;
    }

    private async void PageSize_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await RefreshAsync();
    }
}
