using System.Runtime.InteropServices;

namespace GlobalAutocorrect.Windows.Security;

/// <summary>
/// D142: Windows' own per-user encryption (DPAPI, CryptProtectData). Data protected here can be read back only by this
/// Windows user on this PC: not by other users, not from a copied file, not from a disk taken out. (A program already
/// running as this user could ask Windows to read it, as it could the user's documents: which is why only repeated
/// phrases are kept at all.) An app-specific extra secret is mixed in, so another app's DPAPI data can't be swapped in.
/// </summary>
public static class UserOnlyCrypto
{
    private static readonly byte[] Entropy = "GlobalAutocorrect.Phrases.D142"u8.ToArray();

    public static byte[] Protect(byte[] data) => Run(data, protect: true);

    /// <summary>Null when the data can't be read (another user's, damaged): the caller starts afresh.</summary>
    public static byte[]? Unprotect(byte[] data)
    {
        try { return Run(data, protect: false); }
        catch (CryptographicFailure) { return null; }
    }

    private sealed class CryptographicFailure(int error) : Exception($"DPAPI failed: {error}");

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public nint Data;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, nint description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint mem);

    private static byte[] Run(byte[] data, bool protect)
    {
        var inHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new Blob { Size = Entropy.Length, Data = entHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "Global Autocorrect phrases", ref entropy, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new CryptographicFailure(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            inHandle.Free();
            entHandle.Free();
        }
    }
}
