using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Revalidation contract: the evaluator can be called twice with different inputs and produce
/// coherent verdicts. Pins the plan's "use the evaluator for initial candidate selection AND
/// immediately before dispatch" invariant — a companion that was queued at plan time but
/// dismissed by the user before dispatch must flip to Refused on the second call.
/// </summary>
public class FixPolicyRevalidationTests
{
    [Fact]
    public void CompanionRevokedBetweenPlanAndDispatch_FlipsFromAllowedToRefused()
    {
        var config = new PluginConfiguration();
        config.AudioFixMode = FixMode.ManualApprove;

        var atPlanTime = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.AudioLanguage,
            IssueStatus.Queued,
            FixIntent.Companion,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: true);
        Assert.True(atPlanTime.Allowed);

        // User dismissed the AudioLanguage row between plan and dispatch.
        var atDispatch = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.AudioLanguage,
            IssueStatus.Dismissed,
            FixIntent.Companion,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);
        Assert.False(atDispatch.Allowed);
        Assert.Equal(FixReason.ManualApprovalRequired, atDispatch.Reason);
    }

    [Fact]
    public void PrimaryApprovedButSubtitleCompanionRevoked_PrimaryStillAllowed()
    {
        // The primary transcode fix continues; its subtitle companion drops out of the operation.
        // Pins the plan's "companion no longer approved is removed from the operation and from
        // completion effects" — the primary decision is unchanged when the companion revokes.
        var config = new PluginConfiguration();
        config.TranscodeFixMode = FixMode.Automatic;
        config.SubtitleFixMode = FixMode.ManualApprove;

        var primary = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.Quality,
            IssueStatus.Queued,
            FixIntent.Approved,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);

        var companion = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.SubtitleLanguage,
            IssueStatus.Dismissed,
            FixIntent.Companion,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);

        Assert.True(primary.Allowed);
        Assert.False(companion.Allowed);
        Assert.Equal(FixReason.ManualApprovalRequired, companion.Reason);
    }

    [Fact]
    public void ConfigFlippedToOffAfterQueueSnapshot_RefusesAtDispatch()
    {
        // Simulates the FU-4 per-item recheck the plan requires: the user unticks the fixer
        // between the run's initial queue snapshot and this item's dispatch. Evaluate must
        // refuse under the new mode even if the row is still Queued.
        var config = new PluginConfiguration();
        config.PlayabilityFixMode = FixMode.Automatic;
        var atPlanTime = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.Playability,
            IssueStatus.Queued,
            FixIntent.Scheduled,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);
        Assert.True(atPlanTime.Allowed);

        // User flips PlayabilityFixMode to Off mid-run.
        config.PlayabilityFixMode = FixMode.Off;
        var atDispatch = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.Playability,
            IssueStatus.Queued,
            FixIntent.Scheduled,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);
        Assert.False(atDispatch.Allowed);
        Assert.Equal(FixReason.TypeDisabled, atDispatch.Reason);
    }
}
