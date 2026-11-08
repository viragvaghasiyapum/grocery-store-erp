using System.Globalization;
using GroceryErp.App.Presentation;
using GroceryErp.Core.Numerics;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

// Billing lists a typed name's matches without Enter once the name is long enough: one Gujarati
// letter, else two characters, after an optional quantity and *. A long number waits for Enter,
// since it reads as a barcode. The smoke test checks the list itself.
public sealed class BillingLiveSearchTests
{
    [Theory]
    [InlineData("s", null, "1")]
    [InlineData("sa", "sa", "1")]
    [InlineData("  sa ", "sa", "1")]
    [InlineData("ટ", "ટ", "1")]
    [InlineData("ટા", "ટા", "1")]
    [InlineData("3*sa", "sa", "3")]
    [InlineData("2* ટ", "ટ", "2")]
    [InlineData("૨*ટ", "ટ", "2")]
    [InlineData("1.5*ri", "ri", "1.5")]
    [InlineData("3*s", null, "3")]
    [InlineData("3*", null, "3")]
    [InlineData("*sa", null, "1")]
    [InlineData("x*sa", null, "1")]
    [InlineData("0*sa", null, "1")]
    [InlineData("1234567", "1234567", "1")]
    [InlineData("8901234567890", null, "1")]
    [InlineData("2*8901234567890", null, "2")]
    [InlineData("", null, "1")]
    public void LiveSearchTerm_StartsAtOneGujaratiLetterOrTwoCharacters(string typed, string? expected, string quantity)
    {
        BillingPage.LiveSearchTerm(typed, out Quantity prefix).ShouldBe(expected);
        if (expected is not null) prefix.ShouldBe(new Quantity(decimal.Parse(quantity, CultureInfo.InvariantCulture)));
    }
}
