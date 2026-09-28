namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>Whether a converter is ready to run.</summary>
public enum ConverterAvailability
{
    /// <summary>Converter and its toolchain are present and usable.</summary>
    Ready,

    /// <summary>Toolchain download or extraction pending — call <see cref="IFormatConverter.EnsureToolchainAsync"/>.</summary>
    NeedsDownload,

    /// <summary>Toolchain fetch failed or is unavailable on this platform — cannot fix, only detect.</summary>
    Unavailable
}
