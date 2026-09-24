namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Result of <see cref="FixPolicyEvaluator.Evaluate"/>. A record struct so allocation is
/// stack-local and the pattern-matching syntax at call sites reads naturally
/// (<c>if (policy is { Allowed: false, Reason: FixReason.OutsideLibrary })</c>).
/// </summary>
/// <param name="Allowed">True when the operation may proceed; false when the caller must skip it.</param>
/// <param name="Reason">
/// When <paramref name="Allowed"/> is false, names the first refusal rule that fired. When
/// <paramref name="Allowed"/> is true, <see cref="FixReason.None"/>.
/// </param>
internal readonly record struct PolicyDecision(bool Allowed, FixReason Reason)
{
    /// <summary>Gets the evaluator's "allow" singleton — every rule passed.</summary>
    public static PolicyDecision Allow { get; } = new(true, FixReason.None);

    /// <summary>Helper to construct a refusal with a specific reason.</summary>
    /// <param name="reason">Why the operation is refused.</param>
    /// <returns>A refusal decision carrying the reason.</returns>
    public static PolicyDecision Refuse(FixReason reason) => new(false, reason);
}
