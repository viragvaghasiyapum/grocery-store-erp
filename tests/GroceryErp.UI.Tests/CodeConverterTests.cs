using System.Globalization;
using GroceryErp.App.Presentation;
using GroceryErp.Localization;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

// Codes the database stores (payment modes, salary payment types, vendor ledger entries) show as
// words in both languages, never as the code itself or a missing-text marker. The screen's language
// is not switched here: the UI smoke test switches it at the same time.
public sealed class CodeConverterTests
{
    private static readonly CultureInfo[] Languages = [CultureInfo.GetCultureInfo("en-IN"), CultureInfo.GetCultureInfo("gu-IN")];

    [Theory]
    [InlineData("CASH", "MoneyOut_ModeCash")]
    [InlineData("BANK", "MoneyOut_ModeBank")]
    [InlineData("CARD", "MoneyOut_ModeCard")]
    [InlineData("OTHER", "MoneyOut_ReasonOther")]
    public void MoneyModes_ShowWords(string code, string key)
    {
        ShowsWords(MoneyModeConverter.Text(code), code);
        foreach (CultureInfo language in Languages) ShowsWords(TranslationSource.Instance.Get(key, language), code);
        MoneyModeConverter.Text("UPI").ShouldBe("UPI");
    }

    [Theory]
    [InlineData("SALARY", "MoneyOut_Salary")]
    [InlineData("BONUS", "MoneyOut_Bonus")]
    [InlineData("OVERTIME", "MoneyOut_Overtime")]
    [InlineData("OTHER", "MoneyOut_ReasonOther")]
    public void SalaryPaymentTypes_ShowWords(string code, string key)
    {
        ShowsWords((string)new SalaryKindConverter().Convert(code, typeof(string), null!, CultureInfo.InvariantCulture), code);
        foreach (CultureInfo language in Languages) ShowsWords(TranslationSource.Instance.Get(key, language), code);
    }

    // Every kind GetVendorLedgerPageAsync returns.
    [Theory]
    [InlineData("PURCHASE")]
    [InlineData("PURCHASE_VOID")]
    [InlineData("PAYMENT")]
    [InlineData("VENDOR_RETURN")]
    [InlineData("VENDOR_RETURN_CANCEL")]
    public void VendorLedgerEntries_ShowWords(string code)
    {
        ShowsWords((string)new VendorLedgerKindConverter().Convert(code, typeof(string), null!, CultureInfo.InvariantCulture), code);
        foreach (CultureInfo language in Languages)
            ShowsWords(TranslationSource.Instance.Get($"Purchasing_LedgerKind_{code}", language), code);
    }

    private static void ShowsWords(string text, string code)
    {
        text.ShouldNotBeNullOrWhiteSpace();
        text.ShouldNotBe(code);
        text.ShouldNotContain("⟦");
    }
}
