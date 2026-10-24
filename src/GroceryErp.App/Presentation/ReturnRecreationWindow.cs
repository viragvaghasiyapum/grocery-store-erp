using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

// Shown only when a resold return must recreate a deleted product whose older sale
// lacks the saved fields. Nothing is guessed: each gap is marked for the operator.
public sealed class ReturnRecreationWindow : Window
{
    private readonly HashSet<string> _missing;
    private readonly Grid _fields = new();
    private readonly FormErrorSummary _summary = new();
    private readonly List<FormField> _ordered = [];
    private readonly FormField _skuField;
    private readonly FormField _nameField;
    private readonly FormField _localNameField;
    private readonly FormField _hsnField;
    private readonly FormField _categoryField;
    private readonly FormField _unitField;
    private readonly FormField _taxField;
    private readonly FormField _taxModeField;
    private readonly FormField _trackExpiryField;
    private readonly FormField _looseField;
    private readonly FormField _expiryField;
    private readonly TextBox _sku;
    private readonly TextBox _name;
    private readonly TextBox _localName;
    private readonly TextBox _hsn;
    private readonly ComboBox _category;
    private readonly ComboBox _unit;
    private readonly ComboBox _tax;
    private readonly ComboBox _taxMode;
    // Yes/No choices start empty when the old sale did not save the fact.
    private readonly ComboBox _trackExpiry;
    private readonly ComboBox _isLoose;
    private readonly CheckBox _includesTax;
    private readonly DatePicker _expiry;

    public ReturnRecreationInput? Result { get; private set; }

    public ReturnRecreationWindow(ReturnRecreationDraft draft, ReturnRecreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(options);
        _missing = draft.MissingFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Title = Tr("Sales_RecreateTitle");
        Width = 720;
        Height = 700;
        MinWidth = 600;
        MinHeight = 560;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont");
        NumberSubstitution.SetCultureSource(this, NumberCultureSource.Override);
        NumberSubstitution.SetCultureOverride(this, CultureInfo.GetCultureInfo("en-IN"));
        NumberSubstitution.SetSubstitution(this, NumberSubstitutionMethod.European);

        _sku = new TextBox { Text = draft.SuggestedSku, MaxLength = 20 };
        _name = new TextBox { Text = draft.ProductName, MaxLength = 200 };
        _localName = new TextBox { Text = draft.ProductNameLocal ?? "", MaxLength = 200 };
        _hsn = new TextBox { Text = draft.HsnCode ?? "", MaxLength = 8 };
        _category = Choice(options.Categories, draft.CategoryId);
        _unit = Choice(options.Units, draft.UnitId);
        _tax = Choice(options.TaxRates, draft.TaxRateId);
        _taxMode = Choice([new(0, Tr("Setup_TaxRegular")), new(1, Tr("Setup_TaxComposition")), new(2, Tr("Setup_TaxNone"))],
            draft.TaxMode switch { "REGULAR" => 0, "COMPOSITION" => 1, "NONE" => 2, _ => null });
        _trackExpiry = YesNo(draft.TrackExpiry);
        _isLoose = YesNo(draft.IsLoose);
        _expiry = new DatePicker { SelectedDate = draft.ExpiryDate?.ToDateTime(TimeOnly.MinValue), SelectedDateFormat = DatePickerFormat.Short };
        _includesTax = new CheckBox { IsChecked = draft.PriceIncludesTax, Content = Tr("Sales_RecreatePricing"), Margin = new Thickness(0, 28, 0, 10) };

        _skuField = Field("Sales_RecreateSku", _sku, required: true, "SKU");
        _nameField = Field("Sales_RecreateName", _name, required: true, "product name");
        _localNameField = Field("Sales_RecreateLocalName", _localName, required: false);
        _hsnField = Field("Sales_RecreateHsn", _hsn, required: false, "HSN");
        _categoryField = Field("Sales_RecreateCategory", _category, required: true, "category");
        _unitField = Field("Sales_RecreateUnit", _unit, required: true, "unit");
        _taxField = Field("Sales_RecreateTax", _tax, required: true, "tax rate");
        _taxModeField = Field("Sales_RecreateTaxMode", _taxMode, required: true, "tax mode");
        _trackExpiryField = Field("Sales_RecreateTrackExpiry", _trackExpiry, required: true, "expiry tracking");
        _looseField = Field("Sales_RecreateFractional", _isLoose, required: true, "quantity type");
        _expiryField = Field("Sales_RecreateLotExpiry", _expiry, required: false, "lot expiry");
        LayOutFields();

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Styled(new TextBlock { Text = Title }, "FormTitle"));
        heading.Children.Add(Styled(new TextBlock
        {
            Text = TranslationSource.Instance.Format("Sales_RecreateIntro", draft.ProductName,
                draft.TaxRate.Value.ToString("0.###", CultureInfo.InvariantCulture)),
        }, "FormContext"));
        root.Children.Add(heading);
        Grid.SetRow(_summary, 1);
        root.Children.Add(_summary);
        var scroll = new ScrollViewer
        {
            Content = _fields,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        var footer = Styled(new StackPanel(), "FormFooter");
        var save = new Button { Content = Tr("Sales_RecreateSave"), IsDefault = true, MinWidth = 150 };
        save.Click += Save;
        footer.Children.Add(new Button { Content = Tr("Common_Cancel"), IsCancel = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) });
        footer.Children.Add(save);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => (_ordered.FirstOrDefault(static field => field.Hint is not null) ?? _skuField).FocusInput();
    }

    private static ComboBox Choice(IReadOnlyList<ReturnRecreationOption> items, long? selected) => new()
    {
        ItemsSource = items,
        DisplayMemberPath = nameof(ReturnRecreationOption.Label),
        SelectedValuePath = nameof(ReturnRecreationOption.Id),
        SelectedValue = selected,
    };

    private static ComboBox YesNo(bool? value) =>
        Choice([new(1, Tr("Form_Yes")), new(0, Tr("Form_No"))], value switch { true => 1, false => 0, null => null });

    private static T Styled<T>(T element, string style) where T : FrameworkElement
    {
        element.SetResourceReference(StyleProperty, style);
        return element;
    }

    // A field the legacy sale did not save says so under its input.
    private FormField Field(string labelKey, Control input, bool required, string? missingKey = null)
    {
        var field = new FormField
        {
            Label = Tr(labelKey),
            IsRequired = required,
            Hint = missingKey is not null && _missing.Contains(missingKey) ? Tr("Sales_RecreateReview") : null,
            Content = input,
        };
        _ordered.Add(field);
        return field;
    }

    // Two columns in reading order, then the tax-pricing option in the next free cell.
    private void LayOutFields()
    {
        _fields.ColumnDefinitions.Add(new ColumnDefinition());
        _fields.ColumnDefinitions.Add(new ColumnDefinition());
        int count = _ordered.Count + 1;
        for (int index = 0; index < count; index++)
        {
            if (index % 2 == 0) _fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            UIElement cell = index < _ordered.Count ? _ordered[index] : _includesTax;
            Grid.SetRow(cell, index / 2);
            Grid.SetColumn(cell, index % 2);
            _fields.Children.Add(cell);
        }
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        string? sku = form.Text(_skuField, _sku.Text, required: true, maxLength: 20);
        string? name = form.Text(_nameField, _name.Text, required: true, maxLength: 200);
        string? localName = form.Text(_localNameField, _localName.Text, maxLength: 200);
        long? category = form.Choice(_categoryField, _category.SelectedValue);
        long? unit = form.Choice(_unitField, _unit.SelectedValue);
        long? tax = form.Choice(_taxField, _tax.SelectedValue);
        string? mode = form.Choice(_taxModeField, _taxMode.SelectedValue) switch
        {
            0 => "REGULAR",
            1 => "COMPOSITION",
            2 => "NONE",
            _ => null,
        };
        string? hsn = form.Text(_hsnField, _hsn.Text, required: mode == "REGULAR", maxLength: 8);
        long? trackExpiry = form.Choice(_trackExpiryField, _trackExpiry.SelectedValue);
        long? isLoose = form.Choice(_looseField, _isLoose.SelectedValue);
        DateOnly? expiry = form.Date(_expiryField, _expiry.SelectedDate, required: trackExpiry == 1);
        if (!form.Apply(_fields, _summary)) return;
        Result = new ReturnRecreationInput(sku!, name!, localName, category!.Value, unit!.Value, tax!.Value, mode!, hsn,
            _includesTax.IsChecked == true, trackExpiry == 1, isLoose == 1, "SALEABLE", expiry);
        DialogResult = true;
    }

    private static string Tr(string key) => TranslationSource.Instance[key];
}
