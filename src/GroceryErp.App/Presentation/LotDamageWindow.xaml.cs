using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Records damage for one lot: the lot (given, or searched among lots with stock), available
/// quantity, damaged quantity, reason, recovery, date, the cost and net-loss preview and how the
/// date counts in P&amp;L, then a confirmation.
/// </summary>
public partial class LotDamageWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly InventoryService? _inventory;
    private readonly LatestRequest _search = new();
    private readonly LatestRequest _period = new();
    private readonly DispatcherTimer _searchDelay;
    private readonly string _operationId = OperationGuard.NewId();
    private LotItem? _lot;

    public LotDamageWindow(MoneyOutService service, IClock clock, LotItem lot) : this(service, clock, null, lot)
    {
    }

    /// <summary>Opens the form with a lot search, for damage recorded from Money out.</summary>
    public LotDamageWindow(MoneyOutService service, IClock clock, InventoryService inventory) : this(service, clock, inventory, null)
    {
    }

    private LotDamageWindow(MoneyOutService service, IClock clock, InventoryService? inventory, LotItem? lot)
    {
        ArgumentNullException.ThrowIfNull(clock);
        InitializeComponent();
        _service = service;
        _inventory = inventory;
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await SearchLotsAsync();
        };
        DateBox.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        LotField.Visibility = lot is null ? Visibility.Visible : Visibility.Collapsed;
        if (lot is not null) UseLot(lot);
        Loaded += async (_, _) =>
        {
            if (_lot is null)
            {
                LotSearchBox.Focus();
                await SearchLotsAsync();
            }
            else QuantityField.FocusInput();
            await ShowPeriodAsync();
        };
    }

    private void UseLot(LotItem lot)
    {
        _lot = lot;
        LotText.Text = TranslationSource.Instance.Format("Stock_LotDamageContext",
            lot.ProductName, lot.LotCode, lot.OnHand, lot.UnitCost);
        QuantityField.Hint = TranslationSource.Instance.Format("MoneyOut_DamageAvailable", lot.OnHand);
        ReasonBox.SelectedIndex = lot.ExpiryBucketCode == "EXPIRED" ? 0 : 5;
        UpdatePreview();
    }

    private void LotSearch_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async Task SearchLotsAsync()
    {
        if (_inventory is null) return;
        using LatestRequest.Scope request = _search.Begin();
        try
        {
            LotPage page = await _inventory.GetLotPageAsync(new LotFilter
            {
                Query = InputNormalizer.Normalize(LotSearchBox.Text ?? string.Empty),
                Status = "AVAILABLE",
            }, null, 30, request.Token);
            if (!request.IsCurrent) return;
            LotBox.ItemsSource = page.Items;
            if (page.Items.Count == 1) LotBox.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) Summary.ShowMessage(UserError.Message(exception));
        }
    }

    private void LotBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LotBox.SelectedItem is LotItem lot) UseLot(lot);
    }

    private void Preview_OnChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private async void Date_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await ShowPeriodAsync();
    }

    private async Task ShowPeriodAsync()
    {
        if (DateBox.SelectedDate is not { } date) return;
        using LatestRequest.Scope request = _period.Begin();
        try
        {
            CorrectionDating dating = await _service.PreviewCorrectionAsync(DateOnly.FromDateTime(date), request.Token);
            if (request.IsCurrent) PeriodText.Text = PeriodNotice.ForNewEntry(dating);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) PeriodText.Text = UserError.Message(exception);
        }
    }

    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        var form = new FormValidator();
        Quantity? quantity = form.Quantity(QuantityField, QuantityBox.Text);
        Money? recovered = form.Money(RecoveredField, RecoveredBox.Text);
        if (_lot is null || quantity is null || !form.IsValid)
        {
            PreviewText.Text = string.Empty;
            return;
        }
        Money cost = _lot.UnitCost * quantity.Value;
        Money recoveredAmount = recovered ?? Money.Zero;
        PreviewText.Text = $"{TranslationSource.Instance["MoneyOut_Cost"]}: {cost}  •  " +
            $"{TranslationSource.Instance["MoneyOut_Recovered"]}: {recoveredAmount}  •  " +
            $"{TranslationSource.Instance["Stock_NetLoss"]}: {cost - recoveredAmount}";
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        if (_lot is null) form.AddKey(LotField, "Form_Choose");
        Quantity? quantity = form.Quantity(QuantityField, QuantityBox.Text);
        if (_lot is not null && quantity is { } amount && amount > _lot.OnHand)
            form.AddKey(QuantityField, "Stock_DamageQuantityRange");
        Money? recovered = form.Money(RecoveredField, RecoveredBox.Text);
        DateOnly? date = form.Date(DateField, DateBox.SelectedDate);
        if (!form.Apply(Fields, Summary)) return;
        string reason = (ReasonBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OTHER";
        if (MessageBox.Show(TranslationSource.Instance.Format("MoneyOut_DamageConfirm", quantity!.Value, _lot!.LotCode),
            TranslationSource.Instance["MoneyOut_RecordDamage"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SaveButton.IsEnabled = false;
        try
        {
            await _service.RecordDamageAsync(new(_lot.Id, date!.Value, quantity.Value, reason, recovered ?? Money.Zero,
                NotesBox.Text), _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
