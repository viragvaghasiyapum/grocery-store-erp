using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class VendorPaymentWindow : Window
{
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
    private readonly PurchasingService _purchasing;
    private readonly Stack<(DateOnly? Date, long? Id)> _purchaseBack = new();
    private DateOnly? _purchaseBeforeDate;
    private long? _purchaseBeforeId;
    private VendorPurchasePage? _purchasePage;
    private int _purchaseVersion;
    private bool _purchaseLoading;
    public VendorPaymentWindow(PurchasingService purchasing)
    {
        InitializeComponent();
        _purchasing = purchasing;
        Loaded += async (_, _) =>
        {
            VendorBox.ItemsSource = await _purchasing.GetVendorsAsync(includeInactive: true);
            DateBox.SelectedDate = ((App)Application.Current).Services.GetRequiredService<IClock>()
                .BusinessDate.ToDateTime(TimeOnly.MinValue);
            ModeBox.SelectedIndex = 0;
        };
    }
    private async void VendorBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PurchaseBox is null) return;
        _purchaseBack.Clear();
        _purchaseBeforeDate = null;
        _purchaseBeforeId = null;
        await LoadPurchasesAsync();
    }

    private async void PurchaseSearch_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        int version = ++_purchaseVersion;
        await Task.Delay(180);
        if (version != _purchaseVersion) return;
        _purchaseBack.Clear();
        _purchaseBeforeDate = null;
        _purchaseBeforeId = null;
        await LoadPurchasesAsync();
    }

    private async Task<bool> LoadPurchasesAsync()
    {
        int version = ++_purchaseVersion;
        _purchaseLoading = true;
        PurchasePreviousButton.IsEnabled = false;
        PurchaseNextButton.IsEnabled = false;
        try
        {
            long? vendorId = VendorBox.SelectedValue as long?;
            VendorPurchasePage? page = vendorId is long id
                ? await _purchasing.GetVendorPurchasePageAsync(id, PurchaseSearchBox.Text,
                    _purchaseBeforeDate, _purchaseBeforeId, PageSize) : null;
            if (version != _purchaseVersion) return false;
            _purchasePage = page;
            PurchaseBox.ItemsSource = page?.Items;
            PurchasePreviousButton.IsEnabled = _purchaseBack.Count > 0;
            PurchaseNextButton.IsEnabled = page?.HasMore == true;
            return true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); return false; }
        finally { if (version == _purchaseVersion) _purchaseLoading = false; }
    }

    private async void PurchaseNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (_purchaseLoading || _purchasePage is not { HasMore: true } page) return;
        var previous = (_purchaseBeforeDate, _purchaseBeforeId);
        _purchaseBeforeDate = page.NextDate;
        _purchaseBeforeId = page.NextId;
        if (await LoadPurchasesAsync() &&
            _purchaseBeforeDate == page.NextDate && _purchaseBeforeId == page.NextId)
            _purchaseBack.Push(previous);
        else { _purchaseBeforeDate = previous.Item1; _purchaseBeforeId = previous.Item2; }
        PurchasePreviousButton.IsEnabled = _purchaseBack.Count > 0;
    }

    private async void PurchasePrevious_OnClick(object sender, RoutedEventArgs e)
    {
        if (_purchaseLoading || _purchaseBack.Count == 0) return;
        var previous = _purchaseBack.Peek();
        var current = (_purchaseBeforeDate, _purchaseBeforeId);
        _purchaseBeforeDate = previous.Date;
        _purchaseBeforeId = previous.Id;
        if (await LoadPurchasesAsync()) _purchaseBack.Pop();
        else { _purchaseBeforeDate = current.Item1; _purchaseBeforeId = current.Item2; }
        PurchasePreviousButton.IsEnabled = _purchaseBack.Count > 0;
    }
    private async void PageSize_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _purchaseBack.Clear(); _purchaseBeforeDate = null; _purchaseBeforeId = null;
        await LoadPurchasesAsync();
    }
    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string mode = ((ComboBoxItem)ModeBox.SelectedItem).Content.ToString() ?? "CASH";
            await _purchasing.RecordPaymentAsync(new((long)VendorBox.SelectedValue,
                PurchaseBox.SelectedValue is long purchaseId ? purchaseId : null,
                DateOnly.FromDateTime(DateBox.SelectedDate ?? throw new InvalidOperationException()),
                new Money(decimal.Parse(InputNormalizer.Normalize(AmountBox.Text),
                    NumberStyles.Number, TranslationSource.FormatCulture)), mode, ReferenceBox.Text, NotesBox.Text), OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
