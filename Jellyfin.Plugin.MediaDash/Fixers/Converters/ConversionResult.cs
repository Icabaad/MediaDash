namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Outcome of a single conversion attempt.
/// </summary>
/// <param name="Success">True when the staged output was written and passed the converter's own sanity check.</param>
/// <param name="StagedPath">Absolute path of the produced output file, or null on failure.</param>
/// <param name="Reason">Plain-language description of what happened. Empty on success.</param>
/// <param name="TechnicalDetail">Raw process output (ebook-convert stderr, exception text) when the converter has one to expose.</param>
public sealed record ConversionResult(bool Success, string? StagedPath, string Reason, string? TechnicalDetail = null);
