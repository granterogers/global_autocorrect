using System.IO;
using System.Windows.Input;
using GlobalAutocorrect.App.Tray;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.Core.Settings;
using GlobalAutocorrect.Llm;
using GlobalAutocorrect.Windows.Targets;
using Microsoft.Win32;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Settings;

public class SettingsViewModelTests
{
    // ---- 9.3.a Home ------------------------------------------------------------------------------------------------------

    [Fact]
    public void R9_3_a_HomeShowsTheStatusAndTheSwitchPauses()
    {
        using var s = new FakeServices();
        var vm = new HomeViewModel(s, s);

        Assert.True(vm.IsOn);
        Assert.Equal("On", vm.StatusTitle);
        Assert.Equal("Model loaded", vm.ModelText);
        Assert.Equal("Using GPU: NVIDIA RTX PRO 2000", vm.DeviceText);
        Assert.Equal("Typical clean-up time: 340 ms", vm.LatencyText);
        Assert.Equal("57", vm.CorrectionsText);

        vm.IsOn = false;   // the big switch
        Assert.True(s.Settings.Paused);

        s.ModelState = ServerState.Stopped;
        s.ModelInfo = null;
        s.TypicalLatencyMs = 0;
        vm.Refresh();
        Assert.Equal("Paused", vm.StatusTitle);
        Assert.Equal("Model not loaded", vm.ModelText);
        Assert.Equal("Typical clean-up time: —", vm.LatencyText);
        Assert.Equal("Using CPU", HomeViewModel.DescribeDevice(ServerState.Ready, new ServerInfo(false, null, 0, 43, true)));
    }

    [Fact]
    public void R9_3_a_RefreshDoesNotWriteSettingsBack()
    {
        using var s = new FakeServices();
        var vm = new HomeViewModel(s, s);
        int before = s.Saves;
        vm.Refresh();
        vm.Refresh();
        Assert.Equal(before, s.Saves);
    }

    // ---- 9.3.b Clean-up --------------------------------------------------------------------------------------------------

    [Fact]
    public void R9_3_b_PauseLengthMapsToSettingWithinRange()
    {
        using var s = new FakeServices();
        var vm = new CleanUpViewModel(s, Path.Combine(s.Dir, "models"));
        Assert.Equal(700, vm.PauseMs);

        vm.PauseMs = 1000;
        Assert.Equal(1000, s.Settings.PauseMs);

        vm.PauseMs = 50;     // below the range
        Assert.Equal(300, s.Settings.PauseMs);
        vm.PauseMs = 5000;   // above the range
        Assert.Equal(2000, s.Settings.PauseMs);
        vm.PauseMs = 1234;   // 100 ms steps
        Assert.Equal(1200, s.Settings.PauseMs);
        Assert.Equal(1200, vm.PauseMs);
        Assert.Equal("1.2 seconds", vm.PauseLabel);

        Assert.Equal(300, CleanUpViewModel.NormalisePause(double.NegativeInfinity));
        Assert.Equal(2000, CleanUpViewModel.NormalisePause(double.PositiveInfinity));
    }

    [Fact]
    public void R9_3_b_BoldnessUnderlineAndModelAreSaved()
    {
        using var s = new FakeServices();
        string models = Path.Combine(s.Dir, "models");
        Directory.CreateDirectory(models);
        foreach (var f in new[] { "gemma-4-E4B-it-Q5_K_M.gguf", "gemma-4-E2B-it-Q5_K_M.gguf", "readme.txt" })
            File.WriteAllBytes(Path.Combine(models, f), []);

        var vm = new CleanUpViewModel(s, models);
        Assert.Equal(["Gemma 4 E4B (recommended)", "Gemma 4 E2B, lighter and faster"], vm.Models.Select(m => m.Display).ToArray());
        Assert.Equal("gemma-4-E4B-it-Q5_K_M.gguf", vm.SelectedModel?.File);

        vm.IsBold = true;
        Assert.Equal(Boldness.Bold, s.Settings.Boldness);
        Assert.False(vm.IsStandard);
        vm.IsCautious = true;
        Assert.Equal(Boldness.Cautious, s.Settings.Boldness);

        vm.UnderlineEnabled = false;
        Assert.False(s.Settings.UnderlineEnabled);

        vm.SelectedModel = vm.Models[1];
        Assert.Equal("gemma-4-E2B-it-Q5_K_M.gguf", s.Settings.Model);
    }

    // ---- 9.3.c Prediction ------------------------------------------------------------------------------------------------

    [Fact]
    public void R9_3_c_PredictionSwitchIsSaved()
    {
        using var s = new FakeServices();
        var vm = new PredictionViewModel(s);
        Assert.True(vm.PredictionEnabled);
        vm.PredictionEnabled = false;
        Assert.False(s.Settings.PredictionEnabled);
    }

    // ---- 9.3.d Shortcuts -------------------------------------------------------------------------------------------------

    [Fact]
    public void R9_3_d_ShortcutRecordingRejectsInvalidCombos()
    {
        using var s = new FakeServices();
        var vm = new ShortcutsViewModel(s);
        var pause = vm.Pause;
        Assert.Equal("Ctrl+Alt+P", pause.Text);

        pause.StartRecording();
        Assert.Equal("Press a shortcut…", pause.DisplayText);

        // A modifier on its own keeps waiting and says nothing.
        Assert.True(pause.Record(ModifierKeys.Control, Key.LeftCtrl));
        Assert.False(pause.HasError);

        // No Ctrl/Alt/Win: refused, nothing saved, still recording so the user can try again.
        Assert.True(pause.Record(ModifierKeys.None, Key.P));
        Assert.True(pause.HasError);
        Assert.True(pause.Record(ModifierKeys.Shift, Key.A));
        Assert.True(pause.HasError);
        // A key Hotkey can't use.
        Assert.True(pause.Record(ModifierKeys.Control | ModifierKeys.Alt, Key.Tab));
        Assert.True(pause.HasError);
        // The same as the undo shortcut.
        Assert.True(pause.Record(ModifierKeys.Control | ModifierKeys.Alt, Key.Z));
        Assert.Contains("already used", pause.Error);
        Assert.Equal("Ctrl+Alt+P", s.Settings.PauseShortcut);
        Assert.True(pause.IsRecording);
        Assert.Equal(0, s.Saves);

        // A good one is saved in its canonical form.
        Assert.True(pause.Record(ModifierKeys.Alt | ModifierKeys.Control, Key.F9));
        Assert.False(pause.HasError);
        Assert.False(pause.IsRecording);
        Assert.Equal("Ctrl+Alt+F9", pause.Text);
        Assert.Equal("Ctrl+Alt+F9", s.Settings.PauseShortcut);

        // Not recording: key presses are left alone.
        Assert.False(pause.Record(ModifierKeys.Control, Key.A));

        Assert.False(pause.TrySet("banana"));
        Assert.Equal("Ctrl+Alt+F9", s.Settings.PauseShortcut);

        pause.ResetCommand.Execute(null);
        Assert.Equal("Ctrl+Alt+P", s.Settings.PauseShortcut);

        // Esc cancels recording without changing anything.
        vm.Undo.StartRecording();
        Assert.True(vm.Undo.Record(ModifierKeys.None, Key.Escape));
        Assert.False(vm.Undo.IsRecording);
        Assert.Equal("Ctrl+Alt+Z", s.Settings.UndoShortcut);
    }

    // ---- 9.3.e Apps ------------------------------------------------------------------------------------------------------

    [Fact]
    public void R9_3_e_ExcludedAppsAddRemoveRestore()
    {
        using var s = new FakeServices();
        var vm = new AppsViewModel(s);
        Assert.Null(s.Settings.ExcludedApps);
        Assert.Equal(AppExclusions.Defaults.Count, vm.Apps.Count);   // the built-in list is shown (8.4)

        vm.NewApp = "  Notepad.exe ";
        vm.AddCommand.Execute(null);
        Assert.Contains("Notepad", vm.Apps);
        Assert.Contains("Notepad", s.Settings.ExcludedApps!);
        Assert.Equal(AppExclusions.Defaults.Count + 1, s.Settings.ExcludedApps!.Count);
        Assert.Equal("", vm.NewApp);

        // Duplicates (any case) and things that aren't program names are refused with a message.
        vm.NewApp = "notepad";
        vm.AddCommand.Execute(null);
        Assert.True(vm.HasError);
        Assert.Equal(AppExclusions.Defaults.Count + 1, s.Settings.ExcludedApps!.Count);
        vm.NewApp = @"C:\Windows\notepad.exe";
        vm.AddCommand.Execute(null);
        Assert.True(vm.HasError);
        vm.NewApp = "   ";
        vm.AddCommand.Execute(null);
        Assert.True(vm.HasError);

        vm.RemoveCommand.Execute("Notepad");
        Assert.DoesNotContain("Notepad", vm.Apps);
        Assert.DoesNotContain("Notepad", s.Settings.ExcludedApps!);

        vm.RemoveCommand.Execute("cmd");
        Assert.DoesNotContain("cmd", s.Settings.ExcludedApps!);

        vm.RestoreDefaultsCommand.Execute(null);
        Assert.Null(s.Settings.ExcludedApps);   // null means the built-in list again
        Assert.Contains("cmd", vm.Apps);
        Assert.Equal(AppExclusions.Defaults.Count, vm.Apps.Count);
    }

    // ---- 9.3.f Dictionary ------------------------------------------------------------------------------------------------

    [Fact]
    public void R9_3_f_DictionaryAddRemoveViaViewModel()
    {
        using var s = new FakeServices();
        using var vm = new DictionaryViewModel(s);
        Assert.True(vm.IsEmpty);

        vm.NewWord = "Pontypridd";
        vm.AddCommand.Execute(null);
        Assert.True(s.Dictionary.Contains("pontypridd"));
        Assert.Equal(["Pontypridd"], vm.Words.ToArray());
        Assert.Equal("", vm.NewWord);
        Assert.Equal("1 word", vm.CountText);

        vm.NewWord = "Pontypridd";
        vm.AddCommand.Execute(null);
        Assert.Contains("already", vm.Error);

        vm.NewWord = "two words";
        vm.AddCommand.Execute(null);
        Assert.True(vm.HasError);
        Assert.False(s.Dictionary.Contains("two words"));

        vm.NewWord = "Llanelli";
        vm.AddCommand.Execute(null);
        Assert.Equal(2, vm.Words.Count);

        vm.Search = "llan";
        Assert.Equal(["Llanelli"], vm.Words.ToArray());
        vm.Search = "zzz";
        Assert.True(vm.IsEmpty);
        vm.Search = "";

        vm.RemoveCommand.Execute("Pontypridd");
        Assert.False(s.Dictionary.Contains("Pontypridd"));
        Assert.Equal(["Llanelli"], vm.Words.ToArray());

        // Words added elsewhere (an undo adds one, 4.8.c) show up on refresh.
        s.Dictionary.Add("Maesteg");
        vm.Refresh();
        Assert.Contains("Maesteg", vm.Words);

        Assert.True(vm.AutoLearn);
        vm.AutoLearn = false;
        Assert.False(s.Settings.AutoLearnWords);
    }

    // ---- 9.3.g About -----------------------------------------------------------------------------------------------------

    [Fact]
    public void R9_3_g_AboutReadsMetricsJson()
    {
        using var s = new FakeServices();
        string metrics = Path.Combine(s.Dir, "metrics.json");
        File.WriteAllText(metrics, """
            {"set":"holdout","model":"gemma-4-E4B-it-Q5_K_M.gguf","date":"2026-10-29","messages":200,
             "errorsFixedRate":0.9523,"falseEditRate":0.0041,"latencyP50Ms":346.4}
            """);
        string notice = Path.Combine(s.Dir, "NOTICE.md");
        File.WriteAllText(notice, "# Notices");
        const string Key = @"Software\GlobalAutocorrectTests\About";
        try
        {
            string? opened = null;
            var vm = new AboutViewModel(s, metrics, notice, new StartupRegistration(Key, @"C:\App\GlobalAutocorrect.exe"), p => opened = p);

            Assert.True(vm.HasMetrics);
            Assert.False(vm.HasNoMetrics);
            Assert.Equal("95.2%", vm.Metrics!.ErrorsFixed);
            Assert.Equal("0.40%", vm.Metrics.FalseEdits);   // below 1%: two decimals
            Assert.Equal("346 ms", vm.Metrics.TypicalTime);
            Assert.Equal("Gemma 4 E4B (recommended)", vm.Metrics.Model);
            Assert.Equal("29 October 2026", vm.Metrics.Date);
            Assert.Contains("200 holdout test messages", vm.Metrics.Caption);
            Assert.Equal("Release candidate", vm.Channel);
            Assert.False(string.IsNullOrWhiteSpace(vm.Version));
            Assert.Contains(vm.Version, vm.VersionLine);

            vm.OpenNoticeCommand.Execute(null);
            Assert.Equal(notice, opened);

            // Start with Windows writes the Run value and saves the setting.
            vm.StartWithWindows = false;
            Assert.False(s.Settings.StartWithWindows);
            Assert.False(new StartupRegistration(Key, @"C:\App\GlobalAutocorrect.exe").IsEnabled);
            vm.StartWithWindows = true;
            Assert.True(s.Settings.StartWithWindows);
            Assert.True(new StartupRegistration(Key, @"C:\App\GlobalAutocorrect.exe").IsEnabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\GlobalAutocorrectTests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void R9_3_g_AboutSaysNotMeasuredWhenMetricsAreMissingOrBroken()
    {
        using var s = new FakeServices();
        string missing = Path.Combine(s.Dir, "nope.json");
        string broken = Path.Combine(s.Dir, "broken.json");
        File.WriteAllText(broken, "{ not json");
        string wrongShape = Path.Combine(s.Dir, "other.json");
        File.WriteAllText(wrongShape, """{"hello":1}""");
        var startup = new StartupRegistration(@"Software\GlobalAutocorrectTests\About2", @"C:\App\x.exe");

        foreach (var path in new[] { missing, broken, wrongShape })
        {
            var vm = new AboutViewModel(s, path, Path.Combine(s.Dir, "NOTICE.md"), startup, _ => { });
            Assert.False(vm.HasMetrics);
            Assert.True(vm.HasNoMetrics);
            Assert.Equal("Not measured in this build", vm.NoMetricsText);
            vm.OpenNoticeCommand.Execute(null);   // no credits file: says so, doesn't throw
            Assert.NotEqual("", vm.NoticeMessage);
        }
    }
}
