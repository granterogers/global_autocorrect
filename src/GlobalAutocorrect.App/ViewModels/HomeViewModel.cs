using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.Llm;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>Home (9.3.a): on/paused, whether the model is loaded, GPU or CPU, typical clean-up time, corrections today.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IStatusSource _status;
    private bool _loading;

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusCaption = "";
    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private string _deviceText = "";
    [ObservableProperty] private string _latencyText = "";
    [ObservableProperty] private string _correctionsText = "0";

    public HomeViewModel(ISettingsService settings, IStatusSource status)
    {
        _settings = settings;
        _status = status;
        Refresh();
    }

    /// <summary>The big switch. On means checking your typing; off means paused (8.2).</summary>
    partial void OnIsOnChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(_settings.Settings with { Paused = !value });
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            bool paused = _status.Paused;
            IsOn = !paused;
            StatusTitle = paused ? "Paused" : "On";
            StatusCaption = paused
                ? "Nothing you type is being changed. Switch on to carry on."
                : "Fixing typos and suggesting words as you type.";
            ModelText = DescribeModel(_status.ModelState);
            DeviceText = DescribeDevice(_status.ModelState, _status.ModelInfo);
            LatencyText = DescribeLatency(_status.TypicalLatencyMs);
            CorrectionsText = _status.CorrectionsToday.ToString("N0", CultureInfo.GetCultureInfo("en-GB"));
        }
        finally { _loading = false; }
    }

    public static string DescribeModel(ServerState state) => state switch
    {
        ServerState.Ready => "Model loaded",
        ServerState.Starting => "Model loading…",
        _ => "Model not loaded",
    };

    public static string DescribeDevice(ServerState state, ServerInfo? info)
    {
        if (info is null) return state == ServerState.Ready ? "Using CPU" : "Graphics card: not known yet";
        if (!info.UsingGpu) return "Using CPU";
        string name = FriendlyDevice(info.Device);
        return name.Length == 0 ? "Using GPU" : $"Using GPU: {name}";
    }

    /// <summary>"CUDA0 (NVIDIA RTX PRO 2000 …)" becomes "NVIDIA RTX PRO 2000 …".</summary>
    public static string FriendlyDevice(string? device)
    {
        if (string.IsNullOrWhiteSpace(device)) return "";
        var m = Regex.Match(device, @"\(([^)]+)\)");
        return (m.Success ? m.Groups[1].Value : device).Trim();
    }

    public static string DescribeLatency(double ms) =>
        ms <= 0 ? "Typical clean-up time: —" : $"Typical clean-up time: {Math.Round(ms).ToString("0", CultureInfo.InvariantCulture)} ms";
}
