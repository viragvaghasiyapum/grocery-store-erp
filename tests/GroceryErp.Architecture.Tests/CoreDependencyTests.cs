using GroceryErp.Core.Errors;
using Shouldly;
using Xunit;

namespace GroceryErp.Architecture.Tests;

public sealed class CoreDependencyTests
{
    [Fact]
    public void Core_HasOnlyFrameworkAssemblyReferences()
    {
        List<string> foreignReferences = typeof(DomainException).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .Where(name => name != "System" && name != "netstandard")
            .Where(name => !name!.StartsWith("System.", StringComparison.Ordinal))
            .ToList()!;

        foreignReferences.ShouldBeEmpty();
    }
}
