using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Jharicast.Tests;

public sealed class CoreDependencyTests
{
    // AGENTS.md rule 6: the core has no package references. A reference outside the base class
    // library would reach every consumer of the rules.
    [Fact]
    public void Core_references_only_the_base_class_library()
    {
        var core = Assembly.Load("Jharicast");

        var foreign = core.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name != "System" && name != "netstandard" && !name.StartsWith("System.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(foreign);
    }
}
