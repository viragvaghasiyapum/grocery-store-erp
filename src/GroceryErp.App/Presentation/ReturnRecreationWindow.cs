using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

// Shown only when a resold return must recreate a deleted product whose older sale
// lacks the saved fields. Nothing is guessed: each gap is marked for the operator.
public sealed class ReturnRecreationWindow : Window
{
    private static readonly Brush ErrorBrush = Brushes.Firebrick;
    private static readonly Brush ReviewBrush = new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00));
    private readonly HashSet<string> _missing;
    private readonly TextBox _sku;
    private readonly TextBox _name;
    private readonly TextBox _localName;
    private readonly TextBox _hsn;
    private readonly ComboBox _category;
    private readonly ComboBox _unit;
    private readonly ComboBox _tax;
    private readonly ComboBox _taxMode;
    private readonly CheckBox _includesTax;
    private readonly CheckBox _trackExpiry;
    private readonly CheckBox _isLoose;
    private readonly DatePicker _expiry;
    private readonly TextBlock _error;

    public ReturnRecreationInput? Result { get; private set; }

    public ReturnRecreationWindow(ReturnRecreationDraft draft, ReturnRecreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(options);
        _missing = draft.MissingFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Title = Tr("Sales_RecreateTitle");
        Width = 640;
        Height = 700;
        MinWidth = 560;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NumberSubstitution.SetCultureSource(this, NumberCultureSource.Override);
        NumberSubstitution.SetCultureOverride(this, CultureInfo.GetCultureInfo("en-IN"));
        NumberSubstitution.SetSubstitution(this, NumberSubstitutionMethod.European);

        var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = TranslationSource.Instance.Format("Sales_RecreateIntro", draft.ProductName,
                draft.TaxRate.Value.ToString("0.###", CultureInfo.InvariantCulture)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        _sku = new TextBox { Text = draft.SuggestedSku, MaxLength = 20 };
        _name = new TextBox { Text = draft.ProductName, MaxLength = 200 };
        _localName = new TextBox { Text = draft.ProductNameLocal ?? "", MaxLength = 200 };
        _category = Choice(options.Categories, draft.CategoryId);
        _unit = Choice(options.Units, draft.UnitId);
        _tax = Choice(options.TaxRates, draft.TaxRateId);
        _taxMode = new ComboBox
        {
            ItemsSource = new ReturnRecreationOption[]
            {
                new(0, Tr("Setup_TaxRegular")), new(1, Tr("Setup_TaxComposition")), new(2, Tr("Setup_TaxNone")),
            },
            DisplayMemberPath = nameof(ReturnRecreationOption.Label),
            SelectedIndex = draft.TaxMode switch { "REGULAR" => 0, "COMPOSITION" => 1, "NONE" => 2, _ => -1 },
            Height = 32,
        };
        _hsn = new TextBox { Text = draft.HsnCode ?? "", MaxLength = 8 };
        _includesTax = new CheckBox { IsChecked = draft.PriceIncludesTax, Content = Tr("Sales_RecreatePricing") };
        _trackExpiry = new CheckBox
        {
            IsChecked = draft.TrackExpiry,
            IsThreeState = draft.TrackExpiry is null,
            Content = Tr("Sales_RecreateTrackExpiry")
        };
        _isLoose = new CheckBox
        {
            IsChecked = draft.IsLoose,
            IsThreeState = draft.IsLoose is null,
            Content = Tr("Sales_RecreateFractional")
        };
        _expiry = new DatePicker { SelectedDate = draft.ExpiryDate?.ToDateTime(TimeOnly.MinValue) };
        _trackExpiry.Click += (_, _) => _trackExpiry.IsThreeState = false;
        _isLoose.Click += (_, _) => _isLoose.IsThreeState = false;

        AddField(form, "Sales_RecreateSku", _sku, required: true, "SKU");
        AddField(form, "Sales_RecreateName", _name, required: true, "product name");
        AddField(form, "Sales_RecreateLocalName", _localName, required: false);
        AddField(form, "Sales_RecreateCategory", _category, required: true, "category");
        AddField(form, "Sales_RecreateUnit", _unit, required: true, "unit");
        AddField(form, "Sales_RecreateTax", _tax, required: true, "tax rate");
        AddField(form, "Sales_RecreateTaxMode", _taxMode, required: true, "tax mode");
        AddField(form, "Sales_RecreateHsn", _hsn, required: false, "HSN");
        AddOption(form, _includesTax, null);
        AddOption(form, _trackExpiry, "expiry tracking");
        AddOption(form, _isLoose, "quantity type");
        AddField(form, "Sales_RecreateLotExpiry", _expiry, required: false, "lot expiry");

        _error = new TextBlock { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6) };
        Grid.SetRow(_error, 2);
        root.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = Tr("Common_Cancel"), IsCancel = true, Width = 96 };
        var save = new Button
        {
            Content = Tr("Sales_RecreateSave"),
            IsDefault = true,
            MinWidth = 150,
            Margin = new Thickness(8, 0, 0, 0)
        };
        save.Click += Save;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        Content = root;
        Loaded += (_, _) => FirstMissing()?.Focus();
    }

    private static ComboBox Choice(IReadOnlyList<ReturnRecreationOption> items, long? selected) => new()
    {
        ItemsSource = items,
        DisplayMemberPath = nameof(ReturnRecreationOption.Label),
        SelectedValuePath = nameof(ReturnRecreationOption.Id),
        SelectedValue = selected,
        Height = 32,
    };

    private void AddField(Grid grid, string labelKey, Control input, bool required, string? missingKey = null)
    {
        int row = NextRow(grid);
        bool review = missingKey is not null && _missing.Contains(missingKey);
        var caption = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 8),
            TextWrapping = TextWrapping.Wrap
        };
        caption.Inlines.Add(new System.Windows.Documents.Run(Tr(labelKey)));
        if (required) caption.Inlines.Add(new System.Windows.Documents.Run(" *") { Foreground = ErrorBrush });
        if (review) caption.Inlines.Add(new System.Windows.Documents.Run(" · " + Tr("Sales_RecreateReview"))
        { Foreground = ReviewBrush, FontSize = 11 });
        Grid.SetRow(caption, row);
        grid.Children.Add(caption);
        input.Margin = new Thickness(0, 0, 0, 8);
        if (input is TextBox box) { box.Height = 32; box.VerticalContentAlignment = VerticalAlignment.Center; }
        if (review) input.Tag = "review";
        AutomationProperties.SetName(input, Tr(labelKey));
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
    }

    private void AddOption(Grid grid, CheckBox option, string? missingKey)
    {
        int row = NextRow(grid);
        if (missingKey is not null && _missing.Contains(missingKey))
        {
            option.Foreground = ReviewBrush;
            option.Tag = "review";
        }
        option.Margin = new Thickness(0, 2, 0, 10);
        AutomationProperties.SetName(option, option.Content?.ToString() ?? string.Empty);
        Grid.SetRow(option, row);
        Grid.SetColumn(option, 1);
        grid.Children.Add(option);
    }

    private static int NextRow(Grid grid)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return grid.RowDefinitions.Count - 1;
    }

    private Control? FirstMissing() => new Control[]
        { _sku, _name, _category, _unit, _tax, _taxMode, _hsn, _trackExpiry, _isLoose, _expiry }
        .FirstOrDefault(static control => Equals(control.Tag, "review"));

    private void Save(object sender, RoutedEventArgs e)
    {
        string? mode = _taxMode.SelectedIndex switch { 0 => "REGULAR", 1 => "COMPOSITION", 2 => "NONE", _ => null };
        bool trackExpiry = _trackExpiry.IsChecked == true;
        var invalid = new List<Control>();
        if (string.IsNullOrWhiteSpace(_sku.Text)) invalid.Add(_sku);
        if (string.IsNullOrWhiteSpace(_name.Text)) invalid.Add(_name);
        if (_category.SelectedValue is not long) invalid.Add(_category);
        if (_unit.SelectedValue is not long) invalid.Add(_unit);
        if (_tax.SelectedValue is not long) invalid.Add(_tax);
        if (mode is null) invalid.Add(_taxMode);
        if (mode == "REGULAR" && string.IsNullOrWhiteSpace(_hsn.Text)) invalid.Add(_hsn);
        if (_trackExpiry.IsChecked is null) invalid.Add(_trackExpiry);
        if (_isLoose.IsChecked is null) invalid.Add(_isLoose);
        if (trackExpiry && _expiry.SelectedDate is null) invalid.Add(_expiry);
        foreach (Control control in new Control[] { _sku, _name, _category, _unit, _tax, _taxMode, _hsn, _expiry })
            control.ClearValue(BorderBrushProperty);
        if (invalid.Count > 0)
        {
            foreach (Control control in invalid) control.BorderBrush = ErrorBrush;
            _error.Text = Tr("Sales_RecreateRequired");
            invalid[0].Focus();
            return;
        }
        Result = new ReturnRecreationInput(_sku.Text.Trim(), _name.Text.Trim(),
            string.IsNullOrWhiteSpace(_localName.Text) ? null : _localName.Text.Trim(),
            (long)_category.SelectedValue!, (long)_unit.SelectedValue!, (long)_tax.SelectedValue!, mode!,
            string.IsNullOrWhiteSpace(_hsn.Text) ? null : _hsn.Text.Trim(),
            _includesTax.IsChecked == true, trackExpiry, _isLoose.IsChecked == true, "SALEABLE",
            _expiry.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null);
        DialogResult = true;
    }

    private static string Tr(string key) => TranslationSource.Instance[key];
}
