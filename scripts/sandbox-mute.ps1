# D105: runs inside Windows Sandbox. Mutes its default sound output (Core Audio IAudioEndpointVolume.SetMute), so a test
# run never makes a sound on the user's PC. Setting mute (not toggling it) is safe to run any number of times.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    int f1(); int f2(); int f3(); int f4(); int f5(); int f6(); int f7(); int f8(); int f9(); int f10(); int f11();
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice { int Activate(ref Guid id, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator { int f1(); int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice d); }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
public static class Mute {
    public static bool All() {
        var e = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice d; if (e.GetDefaultAudioEndpoint(0, 1, out d) != 0) return false;   // render, multimedia
        var id = typeof(IAudioEndpointVolume).GUID; object o;
        d.Activate(ref id, 23, IntPtr.Zero, out o);
        var v = (IAudioEndpointVolume)o; var g = Guid.Empty;
        v.SetMute(true, ref g); bool m; v.GetMute(out m); return m;
    }
}
'@
try { "muted: $([Mute]::All())" } catch { "mute failed: $($_.Exception.Message)" }
