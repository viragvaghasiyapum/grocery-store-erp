using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using GroceryErp.Core.Errors;
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
    public void EnglishAndGujarati_UseTheSamePlaceholders()
    {
        // A message formatted with an argument the other language lacks loses that detail, or fails.
        ResourceSet english = SharedStrings.ResourceManager.GetResourceSet(
            CultureInfo.InvariantCulture,
            createIfNotExists: true,
            tryParents: true)!;
        ResourceSet gujarati = SharedStrings.ResourceManager.GetResourceSet(
            CultureInfo.GetCultureInfo("gu-IN"),
            createIfNotExists: true,
            tryParents: true)!;
        Dictionary<string, string?> local = gujarati.Cast<DictionaryEntry>()
            .ToDictionary(static entry => (string)entry.Key, static entry => entry.Value?.ToString());

        english.Cast<DictionaryEntry>()
            .Where(entry => !Placeholders(entry.Value?.ToString())
                .SequenceEqual(Placeholders(local.GetValueOrDefault((string)entry.Key))))
            .Select(static entry => (string)entry.Key)
            .ShouldBeEmpty();
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
    public void GujaratiResources_CallALotBatchNeverFlour()
    {
        // The glossary word for a lot or batch is બેચ; લોટ means flour, which a grocery also sells.
        ResourceSet gujarati = SharedStrings.ResourceManager.GetResourceSet(
            CultureInfo.GetCultureInfo("gu-IN"),
            createIfNotExists: true,
            tryParents: true)!;

        gujarati.Cast<DictionaryEntry>()
            .Where(entry => entry.Value?.ToString()?.Contains("લોટ", StringComparison.Ordinal) == true)
            .Select(entry => (string)entry.Key)
            .ShouldBeEmpty();
    }

    [Fact]
    public void EveryDomainError_HasEnglishAndGujaratiMessage()
    {
        foreach (ErrorCode code in Enum.GetValues<ErrorCode>())
        {
            string key = $"Error_{code}";
            SharedStrings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("en-IN"))
                .ShouldNotBeNullOrWhiteSpace();
            SharedStrings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("gu-IN"))
                .ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void EverySharedResourceKey_ResolvesThroughTranslationSource()
    {
        ResourceSet english = SharedStrings.ResourceManager.GetResourceSet(
            CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!;
        foreach (string key in GetKeys(english))
        {
            TranslationSource.Instance.Get(key, CultureInfo.GetCultureInfo("en-IN"))
                .ShouldNotBe($"⟦{key}⟧");
            TranslationSource.Instance.Get(key, CultureInfo.GetCultureInfo("gu-IN"))
                .ShouldNotBe($"⟦{key}⟧");
        }
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

    private static string[] Placeholders(string? text) => Regex.Matches(text ?? string.Empty, @"\{(\d+)(?:[,:][^}]*)?\}")
        .Select(static match => match.Groups[1].Value)
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] GetKeys(ResourceSet resources) => resources
        .Cast<DictionaryEntry>()
        .Select(entry => entry.Key.ToString()!)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
