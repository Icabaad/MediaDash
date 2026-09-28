using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>Lookup layer over the DI-registered <see cref="IFormatConverter"/> instances.</summary>
public interface IFormatConverterRegistry
{
    /// <summary>Gets every registered converter.</summary>
    IReadOnlyCollection<IFormatConverter> All { get; }

    /// <summary>
    /// Returns the converter for the given key, or null when none is registered.
    /// </summary>
    /// <param name="key">Converter key (see <see cref="IFormatConverter.Key"/>).</param>
    /// <returns>The matching converter, or null.</returns>
    IFormatConverter? Get(string key);
}
