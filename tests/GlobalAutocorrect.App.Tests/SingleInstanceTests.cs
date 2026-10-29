using GlobalAutocorrect.App;
using Xunit;

namespace GlobalAutocorrect.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void R5_6_c_OnlyOneCopyRuns()
    {
        string name = @"Local\GlobalAutocorrect.Test." + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));
    }
}
