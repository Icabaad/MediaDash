namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// Vends immutable snapshots of the live <see cref="PluginConfiguration"/>. Consumers that need
/// a coherent view for the duration of a run call <see cref="GetSnapshot"/> once at run start;
/// consumers that need to observe drift (per-item re-checks inside a long fix loop) call it
/// each iteration. Reading <c>Plugin.Instance!.Configuration</c> directly is not a snapshot —
/// two reads can observe a mid-flight save.
/// </summary>
public interface IPluginConfigurationProvider
{
    /// <summary>Returns a fresh snapshot of the current configuration. Mutating the returned instance does not affect saved config.</summary>
    /// <returns>A configuration snapshot whose mutable arrays are independent copies.</returns>
    PluginConfiguration GetSnapshot();
}
