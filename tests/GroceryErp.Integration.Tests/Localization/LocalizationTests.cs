using System.Collections;
using System.Globalization;
using System.Resources;
using GroceryErp.Localization;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests.Localization;

public sealed class LocalizationTests
{
    [Fact]
    public void EnglishAndGujarati_HaveIdenticalKeySets()
    {
        ResourceSet english = SharedStrings.ResourceManager.GetResourceSet(
            CultureInfo.InvariantCulture,
            createIfNotExists: true,
            tryParents: true)!;
        ResourceSet gujarati = SharedStrings.ResourceManager.GetResourceSet(
            CultureInfo.GetCultureInfo("gu-IN"),
            createIfNotExists: true,
            tryParents: true)!;

        GetKeys(gujarati).ShouldBe(GetKeys(english), ignoreOrder: true);
    }

    [Fact]
    public void GujaratiResources_ContainNoGujaratiDigits()
    {
        ResourceSet gujarati = SharedStrings.ResourceManager.GetResourceSet(
            CultureInfo.GetCultureInfo("gu-IN"),
            createIfNotExists: true,
            tryParents: true)!;

        IEnumerable<char> digits = gujarati.Cast<DictionaryEntry>()
            .SelectMany(entry => entry.Value?.ToString() ?? string.Empty)
            .Where(character => character is >= '\u0AE6' and <= '\u0AEF');

        digits.ShouldBeEmpty();
    }

    [Fact]
    public void DisplayFormatter_UsesEnglishDigitsAndIndianGrouping()
    {
        DisplayFormatter formatter = new();

        formatter.Number(12345678.9m).ShouldBe("1,23,45,678.900");
        formatter.Date(new DateOnly(2026, 9, 22)).ShouldBe("22-09-2026");
    }

    [Fact]
    public void PseudoLocalization_ExpandsTextAndPreservesPlaceholders()
    {
        string transformed = PseudoLocalizer.Transform("Save {0}");

        transformed.ShouldStartWith("[!! ");
        transformed.ShouldContain("Šàvé");
        transformed.ShouldContain("{0}");
        transformed.Length.ShouldBeGreaterThan("Save {0}".Length);
    }

    private static string[] GetKeys(ResourceSet resources) => resources
        .Cast<DictionaryEntry>()
        .Select(entry => entry.Key.ToString()!)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
