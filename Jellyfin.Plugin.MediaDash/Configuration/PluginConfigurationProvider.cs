using System;

namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// Default <see cref="IPluginConfigurationProvider"/> — reads <c>Plugin.Instance.Configuration</c>
/// and hands back a per-call deep clone via <see cref="ConfigChangeInvalidation.Clone"/>. That
/// helper already deep-copies every array in the config, which is exactly the snapshot semantics
/// callers depend on (mutating <c>snap.AllowedAudioLanguages</c> must not alter saved config).
/// Tests inject an alternate source through the internal constructor.
/// </summary>
public sealed class PluginConfigurationProvider : IPluginConfigurationProvider
{
    private readonly Func<PluginConfiguration> _source;

    /// <summary>Initializes a new instance of the <see cref="PluginConfigurationProvider"/> class. Reads live plugin configuration each time <see cref="GetSnapshot"/> is called.</summary>
    public PluginConfigurationProvider()
        : this(() => Plugin.Instance!.Configuration)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PluginConfigurationProvider"/> class with an explicit source. Callers supply a source function so the provider is not tied to <c>Plugin.Instance</c>. Test-only.</summary>
    /// <param name="source">Callback returning the live configuration to snapshot.</param>
    internal PluginConfigurationProvider(Func<PluginConfiguration> source)
    {
        _source = source;
    }

    /// <inheritdoc />
    public PluginConfiguration GetSnapshot() => ConfigChangeInvalidation.Clone(_source());
}
