using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;

namespace Zara.Application.Tests;

[TestClass]
public sealed class LockReminderRuntimeTests
{
    private static readonly int[] TenMinuteReminder = [10];

    [TestMethod]
    [DataRow(30, 18, 39)]
    [DataRow(5, 19, 4)]
    public void Wednesday1910RequestsAndDiagnosesTheExpectedReminder(int minutes, int hour, int minute)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 23, hour, minute, 59, TimeSpan.Zero));
        var audio = new RecordingAudio();
        var events = new List<string>();
        var runtime = new LockReminderRuntime(clock, audio, new LockReminderDiagnostics(events.Add));
        var policy = new UsagePolicySettings(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(DayOfWeek.Wednesday,
                new DailyUsageRestriction(true, new TimeOnly(19, 10), new TimeOnly(23, 0))),
            EmergencyUnlockSettings.Default, []);

        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);
        clock.Advance(TimeSpan.FromSeconds(1));
        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);
        Assert.HasCount(1, audio.Requests);
        Assert.AreEqual(minutes, audio.Requests[0]);
        Assert.IsTrue(audio.Callback!());
        Assert.IsTrue(events.Any(entry => entry.Contains($"due minutes={minutes}", StringComparison.Ordinal)));
        Assert.IsTrue(events.Any(entry => entry.Contains($"play-request minutes={minutes}", StringComparison.Ordinal)));
        clock.Advance(TimeSpan.FromSeconds(1));
        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);
        Assert.HasCount(1, audio.Requests);
    }

    [TestMethod]
    public void SixMinutesUntilLockPlaysFiveMinuteReminderAfterOneMinute()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 23, 19, 4, 0, TimeSpan.Zero));
        var audio = new RecordingAudio();
        var runtime = new LockReminderRuntime(clock, audio);
        var policy = new UsagePolicySettings(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(DayOfWeek.Wednesday,
                new DailyUsageRestriction(true, new TimeOnly(19, 10), new TimeOnly(23, 0))),
            EmergencyUnlockSettings.Default, []);
        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);
        for (int second = 0; second < 60; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);
        }
        Assert.HasCount(1, audio.Requests);
        Assert.AreEqual(5, audio.Requests[0]);
        Assert.IsTrue(audio.Callback!());
        Assert.IsFalse(Snapshot(policy, clock).Evaluation.LockRequired);
    }

    [TestMethod]
    public void DiagnosticsDistinguishSlowLoadingFromAudioFailure()
    {
        var clock = new ManualClock();
        var audio = new RecordingAudio();
        var events = new List<string>();
        var runtime = new LockReminderRuntime(clock, audio, new LockReminderDiagnostics(events.Add));
        StartTenMinuteReminder(runtime, clock, audio, CreatePolicy());
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.IsFalse(audio.Callback!());
        Assert.IsTrue(events.Any(entry => entry.Contains("reason=media-load-late", StringComparison.Ordinal)));
        Assert.IsFalse(events.Any(entry => entry.StartsWith("play-failed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DiagnosticSinkFailureDoesNotPreventAnAnnouncement()
    {
        var clock = new ManualClock();
        var audio = new RecordingAudio();
        var runtime = new LockReminderRuntime(clock, audio,
            new LockReminderDiagnostics(_ => throw new IOException("Diagnostic storage unavailable.")));
        StartTenMinuteReminder(runtime, clock, audio, CreatePolicy());
        Assert.IsTrue(audio.Callback!());
    }
    [TestMethod]
    public void ChangingReservationsCancelsAudioThatIsStillLoading()
    {
        var clock = new ManualClock();
        var audio = new RecordingAudio();
        var runtime = new LockReminderRuntime(clock, audio);
        UsagePolicySettings policy = CreatePolicy();
        StartTenMinuteReminder(runtime, clock, audio, policy);
        Func<bool> originalCallback = audio.Callback!;
        Assert.IsTrue(originalCallback());

        policy = policy.WithReservations(
        [
            new OutOfHoursReservation(
                Guid.NewGuid(),
                new DateOnly(2026, 9, 23),
                new TimeOnly(0, 0),
                new TimeOnly(0, 30),
                string.Empty),
        ]);
        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);

        Assert.IsFalse(originalCallback());
        Assert.AreEqual(1, audio.StopCount);
        CollectionAssert.AreEqual(TenMinuteReminder, audio.Requests);
    }

    [TestMethod]
    public void DisablingAnAnnouncementCancelsItsPendingAudio()
    {
        var clock = new ManualClock();
        var audio = new RecordingAudio();
        var runtime = new LockReminderRuntime(clock, audio);
        UsagePolicySettings policy = CreatePolicy();
        StartTenMinuteReminder(runtime, clock, audio, policy);
        Func<bool> callback = audio.Callback!;

        runtime.Observe(Snapshot(policy, clock), new LockReminderSettings(TenMinutes: false));

        Assert.IsFalse(callback());
        Assert.AreEqual(1, audio.StopCount);
    }

    [TestMethod]
    public void PlaybackFailureDoesNotEscapeOrRepeatOnTheNextRefresh()
    {
        var clock = new ManualClock();
        var audio = new RecordingAudio { FailPlay = true, FailStop = true };
        var runtime = new LockReminderRuntime(clock, audio);
        UsagePolicySettings policy = CreatePolicy();
        StartTenMinuteReminder(runtime, clock, audio, policy);
        clock.Advance(TimeSpan.FromSeconds(1));
        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);

        CollectionAssert.AreEqual(TenMinuteReminder, audio.Requests);
        clock.Advance(TimeSpan.FromMinutes(10));
        UsagePolicyRuntimeSnapshot locked = Snapshot(policy, clock);
        runtime.Observe(locked, LockReminderSettings.Default);
        Assert.IsTrue(locked.Evaluation.LockRequired);
        CollectionAssert.AreEqual(TenMinuteReminder, audio.Requests);
    }

    [TestMethod]
    public void StopAndClockResetInvalidateDeferredPlayback()
    {
        var clock = new ManualClock();
        var audio = new RecordingAudio();
        var runtime = new LockReminderRuntime(clock, audio);
        UsagePolicySettings policy = CreatePolicy();
        StartTenMinuteReminder(runtime, clock, audio, policy);
        Func<bool> callback = audio.Callback!;

        runtime.ResetObservation();
        Assert.IsFalse(callback());
        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);
        CollectionAssert.AreEqual(TenMinuteReminder, audio.Requests);
        runtime.Stop();
        Assert.IsFalse(callback());
    }

    [TestMethod]
    public void SlowMediaLoadingCannotStartAnOutdatedMessage()
    {
        var clock = new ManualClock();
        var audio = new RecordingAudio();
        var runtime = new LockReminderRuntime(clock, audio);
        UsagePolicySettings policy = CreatePolicy();
        StartTenMinuteReminder(runtime, clock, audio, policy);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.IsFalse(audio.Callback!());
    }

    [TestMethod]
    public void PlaybackRechecksTheActualClockBeforeTheNextPolicyRefresh()
    {
        var clock = new ManualClock();
        var audio = new RecordingAudio();
        var runtime = new LockReminderRuntime(clock, audio);
        UsagePolicySettings policy = CreatePolicy();
        StartTenMinuteReminder(runtime, clock, audio, policy);
        clock.JumpWallClock(TimeSpan.FromMinutes(10));

        Assert.IsFalse(audio.Callback!());
    }

    private static void StartTenMinuteReminder(
        LockReminderRuntime runtime,
        ManualClock clock,
        RecordingAudio audio,
        UsagePolicySettings policy)
    {
        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);
        clock.Advance(TimeSpan.FromSeconds(1));
        runtime.Observe(Snapshot(policy, clock), LockReminderSettings.Default);
        CollectionAssert.AreEqual(TenMinuteReminder, audio.Requests);
    }

    private static UsagePolicySettings CreatePolicy() => new(
        WeeklyUsageRestrictionSchedule.Default.WithRestriction(
            DayOfWeek.Wednesday,
            new DailyUsageRestriction(true, new TimeOnly(0, 0), new TimeOnly(3, 0))),
        EmergencyUnlockSettings.Default,
        []);

    private static UsagePolicyRuntimeSnapshot Snapshot(UsagePolicySettings policy, ManualClock clock)
    {
        DateTime now = clock.GetLocalNow().DateTime;
        return new UsagePolicyRuntimeSnapshot(
            policy,
            UsagePolicyEvaluator.Evaluate(policy, now, false),
            now,
            false,
            null,
            UsagePolicyEvaluator.FindNextLockStart(policy, now));
    }

    private sealed class RecordingAudio : ILockReminderAudioPort
    {
        public List<int> Requests { get; } = [];
        public Func<bool>? Callback { get; private set; }
        public int StopCount { get; private set; }
        public bool FailPlay { get; init; }
        public bool FailStop { get; init; }

        public void Play(int minutes, Func<bool> isStillValid)
        {
            Requests.Add(minutes);
            Callback = isStillValid;
            if (FailPlay)
            {
                throw new IOException("Audio is unavailable.");
            }
        }

        public void StopPlayback()
        {
            StopCount++;
            if (FailStop)
            {
                throw new InvalidOperationException("Audio is unavailable.");
            }
        }
    }

    private sealed class ManualClock(DateTimeOffset? start = null) : TimeProvider
    {
        private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 9, 22, 23, 49, 59, TimeSpan.Zero);
        private long _timestamp;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            _timestamp += elapsed.Ticks;
        }

        public void JumpWallClock(TimeSpan elapsed) => _now += elapsed;
    }
}
