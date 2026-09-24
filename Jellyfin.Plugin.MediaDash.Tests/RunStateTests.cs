using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Invariants for <see cref="RunState"/> — the DI-scoped owner of every transient run flag that
/// used to live as a static on Plugin / ScanTask / FixTask. Two goals: mutual exclusion within a
/// role (Scan can't start twice) is proven under actual concurrency (barrier-driven, not
/// wall-clock), and one-shot flags are consumed exactly once.
/// </summary>
public class RunStateTests
{
    [Fact]
    public void TryBegin_ReturnsFalseWhenAlreadyRunning()
    {
        var state = new RunState();
        Assert.True(state.TryBegin(RunRole.Scan));
        Assert.False(state.TryBegin(RunRole.Scan));
    }

    [Fact]
    public void TryBegin_ScanAndFix_AreIndependent()
    {
        // No shared "busy" bool — a scan being live does not block a fix and vice versa.
        var state = new RunState();
        Assert.True(state.TryBegin(RunRole.Scan));
        Assert.True(state.TryBegin(RunRole.Fix));
    }

    [Fact]
    public async Task TryBegin_ConcurrentCallers_ExactlyOneWins()
    {
        // Barrier-driven race: 32 tasks all wait on the same TaskCompletionSource, then attempt
        // TryBegin at once. Exactly one may succeed.
        var state = new RunState();
        var barrier = new TaskCompletionSource();
        var winners = 0;
        async Task Attempt()
        {
            await barrier.Task.ConfigureAwait(false);
            if (state.TryBegin(RunRole.Scan))
            {
                Interlocked.Increment(ref winners);
            }
        }

        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(Attempt)).ToArray();
        barrier.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(1, winners);
    }

    [Fact]
    public void FailedTryBegin_DoesNotConsumeOtherRunState()
    {
        // A second TryBegin returning false must not clear the current run's fields.
        var state = new RunState();
        state.ArmBypassIdleCheck(RunRole.Scan);
        Assert.True(state.TryBegin(RunRole.Scan));
        state.UpdateProgress(RunRole.Scan, "/x", "L", 3, 10);

        Assert.False(state.TryBegin(RunRole.Scan));

        var snap = state.ReadSnapshot(RunRole.Scan);
        Assert.True(snap.IsRunning);
        Assert.Equal("/x", snap.CurrentPath);
        Assert.Equal(3, snap.Processed);
        // Bypass is a one-shot for the NEXT begin — the failed TryBegin above didn't run any of
        // the "consume on start" logic (bypass is consumed at ExecuteAsync time, not TryBegin).
    }

    [Fact]
    public void UpdateProgress_IgnoredWhenSlotIsIdle()
    {
        // Prevents a runaway writer (e.g. a scanner whose finally didn't run cleanly) from
        // publishing state after Complete.
        var state = new RunState();
        state.UpdateProgress(RunRole.Fix, "/x", "L", 1, 2);
        var snap = state.ReadSnapshot(RunRole.Fix);
        Assert.False(snap.IsRunning);
        Assert.Null(snap.CurrentPath);
        Assert.Equal(0, snap.Processed);
    }

    [Fact]
    public void Complete_ClearsProgressButKeepsPauseReason()
    {
        // PauseReason is deliberately sticky post-run so the dashboard can surface why a run
        // stopped early. TryBegin (next run start) clears it.
        var state = new RunState();
        state.TryBegin(RunRole.Fix);
        state.SetPauseReason(RunRole.Fix, "Bin full");
        state.UpdateProgress(RunRole.Fix, "/x", "L", 3, 10);
        state.Complete(RunRole.Fix);

        var afterComplete = state.ReadSnapshot(RunRole.Fix);
        Assert.False(afterComplete.IsRunning);
        Assert.Null(afterComplete.CurrentPath);
        Assert.Equal(0, afterComplete.Processed);
        Assert.Equal("Bin full", afterComplete.PauseReason);

        Assert.True(state.TryBegin(RunRole.Fix));
        var afterRebegin = state.ReadSnapshot(RunRole.Fix);
        Assert.True(afterRebegin.IsRunning);
        Assert.Null(afterRebegin.PauseReason);
    }

    [Fact]
    public void ConsumeBypassIdleCheck_OneShot()
    {
        var state = new RunState();
        state.ArmBypassIdleCheck(RunRole.Fix);
        Assert.True(state.ConsumeBypassIdleCheck(RunRole.Fix));
        Assert.False(state.ConsumeBypassIdleCheck(RunRole.Fix));
    }

    [Fact]
    public void ConsumeBypassIdleCheck_ScanFixIndependent()
    {
        var state = new RunState();
        state.ArmBypassIdleCheck(RunRole.Scan);
        Assert.False(state.ConsumeBypassIdleCheck(RunRole.Fix));
        Assert.True(state.ConsumeBypassIdleCheck(RunRole.Scan));
    }

    [Fact]
    public void ConsumeNextScanTypeFilter_OneShot()
    {
        var state = new RunState();
        var filter = (IReadOnlySet<IssueType>)new HashSet<IssueType> { IssueType.Playability };
        state.ArmNextScanTypeFilter(filter);

        var first = state.ConsumeNextScanTypeFilter();
        Assert.NotNull(first);
        Assert.Contains(IssueType.Playability, first!);

        var second = state.ConsumeNextScanTypeFilter();
        Assert.Null(second);
    }

    [Fact]
    public void FixIgnoreActivity_SetOnCommand_ClearedByTryBeginOfNextRun()
    {
        var state = new RunState();
        state.TryBegin(RunRole.Fix);
        state.SetFixIgnoreActivity();
        Assert.True(state.FixIgnoreActivity);

        state.Complete(RunRole.Fix);
        Assert.False(state.FixIgnoreActivity);

        // Simulate the "second manual click" path: arm ignore-activity, then start a fresh run.
        state.SetFixIgnoreActivity();
        state.TryBegin(RunRole.Fix);
        Assert.False(state.FixIgnoreActivity);
    }

    [Fact]
    public void RecordManualScanCompleted_UsesInjectedClock()
    {
        // Injected TimeProvider proves the grace-window arithmetic does not read the wall clock.
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var state = new RunState(fake);
        state.RecordManualScanCompleted();

        var peeked = state.PeekManualScanCompleted();
        Assert.NotNull(peeked);
        Assert.Equal(new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc), peeked!.Value);
    }

    [Fact]
    public void LowImpactHint_EmitsOnceThenSuppresses()
    {
        var state = new RunState();
        Assert.True(state.TryEmitLowImpactHint());
        Assert.False(state.TryEmitLowImpactHint());

        state.ResetLowImpactHintForTests();
        Assert.True(state.TryEmitLowImpactHint());
    }

    [Fact]
    public async Task ReadSnapshot_IsAtomicAcrossFields()
    {
        // A writer racing a reader must never expose a torn snapshot (path from one write and
        // progress from another). Under the shared lock, ReadSnapshot serialises with
        // UpdateProgress; the assertion is that the progress ratio matches the label at all times.
        var state = new RunState();
        state.TryBegin(RunRole.Scan);
        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                state.UpdateProgress(RunRole.Scan, "P" + i, "L" + i, i, i);
            }
        });

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snap = state.ReadSnapshot(RunRole.Scan);
                // The invariant we enforce is that Processed == Total at read time; every writer
                // set them to the same value in one lock-guarded call.
                Assert.Equal(snap.Processed, snap.Total);
            }
        });

        await Task.WhenAll(writer, reader);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) { _now += by; }
    }
}
