using System.Globalization;
using Zara.Infrastructure.Windows;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsLockReminderLogTests
{
    private string _directoryPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directoryPath = Path.Combine(
            Path.GetTempPath(),
            "zara-lock-reminder-log-tests",
            Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task WritesLocalTimestampProcessIdAndMessage()
    {
        using var log = new WindowsLockReminderLog(_directoryPath);
        log.Write("reminder dispatched");
        await log.FlushAsync();

        string line = (await File.ReadAllLinesAsync(CurrentPath())).Single();
        string timestamp = line.Split(' ', 2)[0];
        Assert.IsTrue(DateTimeOffset.TryParseExact(
            timestamp,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTimeOffset parsed));
        Assert.AreEqual(DateTimeOffset.Now.Offset, parsed.Offset);
        StringAssert.Contains(line, $"[pid={Environment.ProcessId}] reminder dispatched");
    }

    [TestMethod]
    public async Task RotationKeepsOnlyCurrentAndPreviousLatestEvents()
    {
        using var log = new WindowsLockReminderLog(_directoryPath, maxFileBytes: 256);
        log.Write("first-event " + new string('a', 100));
        await log.FlushAsync();
        log.Write("second-event " + new string('b', 100));
        await log.FlushAsync();
        log.Write("third-event " + new string('c', 100));
        await log.FlushAsync();

        string current = await File.ReadAllTextAsync(CurrentPath());
        string previous = await File.ReadAllTextAsync(PreviousPath());
        StringAssert.Contains(previous, "second-event");
        StringAssert.Contains(current, "third-event");
        Assert.IsFalse(previous.Contains("first-event", StringComparison.Ordinal));
        Assert.IsLessThanOrEqualTo(256L, new FileInfo(CurrentPath()).Length);
        Assert.IsLessThanOrEqualTo(256L, new FileInfo(PreviousPath()).Length);
        Assert.HasCount(2, Directory.GetFiles(_directoryPath));
    }

    [TestMethod]
    public async Task FileFailureDoesNotEscapeAndNextEventRetries()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_directoryPath)!);
        await File.WriteAllTextAsync(_directoryPath, "blocks directory creation");
        try
        {
            using var log = new WindowsLockReminderLog(_directoryPath);
            log.Write("cannot write yet");
            await log.FlushAsync();
            Assert.IsNotNull(log.LastFailure);

            File.Delete(_directoryPath);
            log.Write("write after recovery");
            await log.FlushAsync();

            Assert.IsNull(log.LastFailure);
            StringAssert.Contains(await File.ReadAllTextAsync(CurrentPath()), "write after recovery");
        }
        finally
        {
            if (File.Exists(_directoryPath))
            {
                File.Delete(_directoryPath);
            }
        }
    }

    [TestMethod]
    public async Task WriteAfterDisposeDoesNothing()
    {
        var log = new WindowsLockReminderLog(_directoryPath);
        log.Write("before dispose");
        log.Dispose();
        string original = await File.ReadAllTextAsync(CurrentPath());

        log.Write("after dispose");
        Assert.AreEqual(original, await File.ReadAllTextAsync(CurrentPath()));
    }

    private string CurrentPath() => Path.Combine(_directoryPath, "lock-reminders.log");

    private string PreviousPath() => Path.Combine(_directoryPath, "lock-reminders.previous.log");
}
