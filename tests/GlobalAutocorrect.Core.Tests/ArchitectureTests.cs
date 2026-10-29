using System.Reflection;
using System.Runtime.Versioning;
using GlobalAutocorrect.Core;
using Xunit;

namespace GlobalAutocorrect.Core.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void R10_1_CoreHasNoWindowsReferences()
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        string[] forbiddenPrefixes =
        [
            "PresentationFramework",
            "WindowsBase",
            "System.Windows",
            "GlobalAutocorrect.Windows",
        ];

        foreach (var name in assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty))
        {
            Assert.DoesNotContain(
                forbiddenPrefixes,
                prefix => name.StartsWith(prefix, StringComparison.Ordinal));
        }

        var tfm = assembly.GetCustomAttribute<TargetFrameworkAttribute>();
        Assert.NotNull(tfm);
        Assert.Equal(".NETCoreApp,Version=v10.0", tfm.FrameworkName);
        Assert.DoesNotContain("-windows", tfm.FrameworkName, StringComparison.Ordinal);
    }
}
