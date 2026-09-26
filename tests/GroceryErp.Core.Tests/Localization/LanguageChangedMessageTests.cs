using GroceryErp.Core.Localization;
using Shouldly;
using Xunit;

namespace GroceryErp.Core.Tests.Localization;

public sealed class LanguageChangedMessageTests
{
    [Fact]
    public void CultureName_IsPreserved()
    {
        LanguageChangedMessage message = new("gu-IN");

        message.CultureName.ShouldBe("gu-IN");
    }
}
