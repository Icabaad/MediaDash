using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// xunit collection marker for tests that instantiate <see cref="Data.MediaDashDb"/> and call
/// <c>SqliteConnection.ClearAllPools()</c> in Dispose. Running those tests in parallel triggered
/// a race where one class's ClearAllPools closed another class's live pooled connection, causing
/// the losing test's <c>File.Delete</c> in Dispose to hit "the process cannot access the file"
/// IOException. Serializing them via a shared collection is cheaper than switching every raw
/// connection to <c>Pooling=False</c> or wrapping every Dispose in try/catch.
/// </summary>
[CollectionDefinition("MediaDashDb", DisableParallelization = true)]
public sealed class MediaDashDbCollection
{
}

/// <summary>
/// Serializes tests that mutate the process-wide static <see cref="Api.Diagnostics"/> buffer.
/// Review P2-6 documented a race between <c>DiagnosticsTests</c> (which clears the buffer and
/// expects exactly N records) and <c>FixTaskLowImpactHintTests</c> (which emits a low-impact
/// hint through the same buffer). Resetting statics in each class's constructor doesn't help
/// once xunit runs them concurrently — this collection is the isolation.
/// </summary>
[CollectionDefinition("Diagnostics", DisableParallelization = true)]
public sealed class DiagnosticsCollection
{
}
