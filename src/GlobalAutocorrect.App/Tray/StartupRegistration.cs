using System.IO;
using Microsoft.Win32;

namespace GlobalAutocorrect.App.Tray;

/// <summary>
/// Start with Windows (8.1): a per-user Run key value, no admin rights and no scheduled task. The key path and exe path are
/// constructor parameters so tests can use a scratch key.
/// </summary>
public sealed class StartupRegistration
{
    public const string ValueName = "GlobalAutocorrect";
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _keyPath;
    private readonly string _exePath;

    public StartupRegistration(string keyPath = DefaultKeyPath, string? exePath = null)
    {
        _keyPath = keyPath;
        _exePath = exePath ?? Environment.ProcessPath ?? "";
    }

    /// <summary>The command line Windows runs at sign-in: the quoted exe path.</summary>
    public string Command => "\"" + _exePath + "\"";

    /// <summary>The stored command, or null when start-with-Windows is off.</summary>
    public string? RegisteredCommand
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                return key?.GetValue(ValueName) as string;
            }
            catch (Exception e) when (IsRegistryFailure(e)) { return null; }
        }
    }

    public bool IsEnabled => RegisteredCommand is not null;

    /// <summary>Writes or removes the Run value. Returns false (and changes nothing) if the registry refused, e.g. by policy.</summary>
    public bool Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (_exePath.Length == 0) return false;
                using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
                if (key is null) return false;
                if (key.GetValue(ValueName) as string != Command) key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception e) when (IsRegistryFailure(e)) { return false; }
    }

    private static bool IsRegistryFailure(Exception e) =>
        e is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException;
}
