using System.Text;
using GlobalAutocorrect.Windows.Security;
using Xunit;

namespace GlobalAutocorrect.Windows.Tests;

/// <summary>D142: the phrases file is unreadable as it sits on disk, and reads back only for this Windows user.</summary>
public sealed class UserOnlyCryptoTests
{
    [Fact]
    public void D142_ProtectedDataIsNotPlainTextAndComesBackIntact()
    {
        var plain = Encoding.UTF8.GetBytes("let me know if you have any questions");
        var sealedBytes = UserOnlyCrypto.Protect(plain);
        Assert.DoesNotContain("questions", Encoding.UTF8.GetString(sealedBytes));
        Assert.Equal(plain, UserOnlyCrypto.Unprotect(sealedBytes));
    }

    [Fact]
    public void D142_DamagedDataReadsAsNothingNotACrash()
    {
        var sealedBytes = UserOnlyCrypto.Protect("thanks for getting back to me"u8.ToArray());
        sealedBytes[^5] ^= 0xFF;
        Assert.Null(UserOnlyCrypto.Unprotect(sealedBytes));
    }
}
