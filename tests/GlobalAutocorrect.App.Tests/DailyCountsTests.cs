using System.IO;
using GlobalAutocorrect.App;
using GlobalAutocorrect.Core.Telemetry;
using Xunit;
using Path = System.IO.Path;

namespace GlobalAutocorrect.App.Tests;

public sealed class DailyCountsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "ga-counts-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { if (File.Exists(_file)) File.Delete(_file); }

    [Fact]
    public void R9_3_a_CorrectionsTodayResetsAtMidnightWhileRunning_R6_5()
    {
        var clock = new DateTime(2026, 10, 29, 23, 59, 0);
        var daily = new DailyCounts(_file, () => clock);
        daily.Load();
        var c = new Counters();
        c.Instant(); c.Instant(); c.Ai();
        Assert.Equal(3, daily.CorrectionsToday(c));
        daily.Save(c);

        clock = clock.AddMinutes(2);   // past midnight, app still running
        Assert.Equal(0, daily.CorrectionsToday(c));
        c.Instant();
        Assert.Equal(1, daily.CorrectionsToday(c));
        daily.Save(c);

        Assert.Equal(3, daily.Days["2026-10-29"]["instantCorrections"] + daily.Days["2026-10-29"]["aiCorrections"]);
        Assert.Equal(1, daily.Days["2026-10-30"]["instantCorrections"]);
    }

    [Fact]
    public void R6_5_CountsCarryAcrossRestartsAndEarlierDaysAreKept()
    {
        var day = new DateTime(2026, 10, 29, 10, 0, 0);
        var first = new DailyCounts(_file, () => day);
        first.Load();
        var c1 = new Counters();
        c1.Instant(); c1.Undo();
        first.Save(c1);

        var second = new DailyCounts(_file, () => day.AddHours(2));   // restarted later the same day
        second.Load();
        var c2 = new Counters();
        c2.Ai();
        Assert.Equal(2, second.CorrectionsToday(c2));
        second.Save(c2);

        var nextDay = new DailyCounts(_file, () => day.AddDays(1));
        nextDay.Load();
        Assert.Equal(0, nextDay.CorrectionsToday(new Counters()));
        Assert.True(nextDay.Days.ContainsKey("2026-10-29"));   // the record of earlier days survives (6.5)
        Assert.Equal(1, nextDay.Days["2026-10-29"]["undos"]);
    }

    [Fact]
    public void R8_6_CountsFileHoldsNoText()
    {
        var daily = new DailyCounts(_file);
        daily.Load();
        var c = new Counters();
        c.Instant();
        daily.Save(c);
        Assert.Matches("^[{}\\[\\]\":,a-zA-Z0-9-]+$", File.ReadAllText(_file));
    }
}
