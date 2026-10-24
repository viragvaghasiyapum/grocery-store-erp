using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>Records a payment to a vendor, optionally against one of its bills (searched in small keyset pages).</summary>
public partial class VendorPaymentWindow : Window
{
    private const int PurchasePageSize = 20;
    private readonly PurchasingService _purchasing;
    private readonly long? _vendorId;
    private readonly KeysetPager<DateCursor> _pager = new();
    private readonly LatestRequest _requests = new();
    private readonly DispatcherTimer _searchDelay;
    private readonly string _operationId = OperationGuard.NewId();

    public VendorPaymentWindow(PurchasingService purchasing, long? vendorId = null)
    {
        InitializeComponent();
        _purchasing = purchasing;
        _vendorId = vendorId;
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await LoadPurchasesAsync(PageMove.First);
        };
        Loaded += async (_, _) =>
        {
            try
            {
                VendorBox.ItemsSource = await _purchasing.GetVendorsAsync(includeInactive: true);
                if (_vendorId is long id) VendorBox.SelectedValue = id;
                DateBox.SelectedDate = ((Application.Current as App)?.ServicesOrNull?.GetService<IClock>()?.BusinessDate
                    ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
                (_vendorId is null ? VendorField : AmountField).FocusInput();
            }
            catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
        };
    }

    private async void VendorBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await LoadPurchasesAsync(PageMove.First);

    private void PurchaseSearch_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async Task LoadPurchasesAsync(PageMove move)
    {
        if (!_pager.CanMove(move)) return;
        using LatestRequest.Scope request = _requests.Begin();
        PurchasePreviousButton.IsEnabled = false;
        PurchaseNextButton.IsEnabled = false;
        try
        {
            if (VendorBox.SelectedValue is not long vendorId)
            {
                PurchaseBox.ItemsSource = null;
                _pager.Reset();
                return;
            }
            DateCursor? cursor = _pager.CursorFor(move);
            VendorPurchasePage page = await _purchasing.GetVendorPurchasePageAsync(vendorId,
                InputNormalizer.Normalize(PurchaseSearchBox.Text ?? string.Empty), cursor?.Date, cursor?.Id, PurchasePageSize);
            if (!request.IsCurrent) return;
            _pager.Commit(move, page.HasMore, page.NextDate is { } date && page.NextId is { } id ? new DateCursor(date, id) : null);
            PurchaseBox.ItemsSource = page.Items;
        }
        catch (Exception exception)
        {
            if (request.IsCurrent) Summary.ShowMessage(UserError.Message(exception));
        }
        finally
        {
            if (request.IsCurrent)
            {
                PurchasePreviousButton.IsEnabled = _pager.CanGoBack;
                PurchaseNextButton.IsEnabled = _pager.HasMore;
            }
        }
    }

    private async void PurchaseNext_OnClick(object sender, RoutedEventArgs e) => await LoadPurchasesAsync(PageMove.Next);

    private async void PurchasePrevious_OnClick(object sender, RoutedEventArgs e) => await LoadPurchasesAsync(PageMove.Previous);

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        long? vendorId = form.Choice(VendorField, VendorBox.SelectedValue);
        DateOnly? date = form.Date(DateField, DateBox.SelectedDate);
        Money? amount = form.Money(AmountField, AmountBox.Text, required: true, positive: true);
        if (!form.Apply(Fields, Summary)) return;
        string mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "CASH";
        SaveButton.IsEnabled = false;
        try
        {
            await _purchasing.RecordPaymentAsync(new(vendorId!.Value, PurchaseBox.SelectedValue as long?, date!.Value,
                amount!.Value, mode, ReferenceBox.Text, NotesBox.Text), _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
