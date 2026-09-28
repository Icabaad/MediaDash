using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <inheritdoc />
public sealed class FormatConverterRegistry : IFormatConverterRegistry
{
    private readonly Dictionary<string, IFormatConverter> _byKey;

    /// <summary>Initializes a new instance of the <see cref="FormatConverterRegistry"/> class. DI-fed constructor; every IFormatConverter registered with the container lands here.</summary>
    /// <param name="converters">All converters resolved from DI.</param>
    public FormatConverterRegistry(IEnumerable<IFormatConverter> converters)
    {
        var list = converters.ToList();
        All = list;
        _byKey = list.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<IFormatConverter> All { get; }

    /// <inheritdoc />
    public IFormatConverter? Get(string key)
        => _byKey.TryGetValue(key, out var c) ? c : null;
}
