using NUnit.Framework;
using SharpFAI.Editor;

namespace SharpFAI.Editor.Tests;

[TestFixture]
public sealed class EditorChartFilesTests
{
    private DirectoryInfo _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory("sharpfai-chart-files-");

    [TearDown]
    public void TearDown() => _directory.Delete(recursive: true);

    [Test]
    public void AMinuteCreatesARollingSnapshotWithoutSavingTheOriginalChart()
    {
        string source = Path.Combine(_directory.FullName, "谱面.adofai");
        var chart = new EditorDocument();
        chart.Save(source);
        string original = File.ReadAllText(source);
        var clock = new TestClock();
        var backup = new EditorAutoBackup(Path.Combine(_directory.FullName, "fallback"), clock);
        backup.Update(chart);
        chart.Set(chart.Settings, "song", "First edit");

        clock.Seconds = 59;
        backup.Update(chart);
        Assert.That(_directory.GetFiles("*.sharpfai-backup-*.adofai"), Is.Empty);

        clock.Seconds = 60;
        backup.Update(chart);
        chart.Set(chart.Settings, "song", "Second edit");
        backup.CompleteWrite(wait: true);
        var copy = _directory.GetFiles("*.sharpfai-backup-*.adofai").Single();
        Assert.That(new EditorDocument(copy.FullName).Settings.Value<string>("song"), Is.EqualTo("First edit"));

        clock.Seconds = 120;
        backup.Update(chart);
        backup.CompleteWrite(wait: true);
        Assert.That(_directory.GetFiles("*.sharpfai-backup-*.adofai").Single().FullName, Is.EqualTo(copy.FullName));
        Assert.That(new EditorDocument(copy.FullName).Settings.Value<string>("song"), Is.EqualTo("Second edit"));
        Assert.That(File.ReadAllText(source), Is.EqualTo(original));
        Assert.That(chart.Path, Is.EqualTo(source));
        Assert.That(chart.Modified, Is.True);
    }

    [Test]
    public void UntitledChartsAreBackedUpInApplicationStorageOnlyAfterEditing()
    {
        var chart = new EditorDocument();
        var clock = new TestClock();
        string destination = Path.Combine(_directory.FullName, "Backups");
        var backup = new EditorAutoBackup(destination, clock);
        backup.Update(chart);
        clock.Seconds = 60;
        backup.Update(chart);
        backup.CompleteWrite(wait: true);
        Assert.That(Directory.Exists(destination), Is.False);

        chart.Set(chart.Settings, "song", "An unsaved chart");
        clock.Seconds = 120;
        backup.Update(chart);
        backup.CompleteWrite(wait: true);
        var reopened = new EditorDocument(Directory.GetFiles(destination, "*.adofai").Single());
        Assert.That(reopened.Settings.Value<string>("song"), Is.EqualTo("An unsaved chart"));
        Assert.That(chart.Path, Is.Null);
        Assert.That(chart.Modified, Is.True);
    }

    [Test]
    public void LastOpenedChartPersistsAcrossSessionsAndCanBeReopened()
    {
        string record = Path.Combine(_directory.FullName, "prefs", "last-chart.txt");
        string chart = Path.Combine(_directory.FullName, "上次的谱面 🌀.adofai");
        var history = new LastOpenedChart(record);
        Assert.That(history.Read(), Is.Null);
        new EditorDocument().Save(chart);

        history.Remember(Path.Combine(_directory.FullName, "previous.adofai"));
        history.Remember(chart);
        string? remembered = new LastOpenedChart(record).Read();
        Assert.That(remembered, Is.EqualTo(chart));
        Assert.That(new EditorDocument(remembered).Path, Is.EqualTo(chart));
    }

    private sealed class TestClock : TimeProvider
    {
        public long Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
}
