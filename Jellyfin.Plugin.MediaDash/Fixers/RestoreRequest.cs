namespace Jellyfin.Plugin.MediaDash.Fixers;

// The typed input to <see cref="RestoreService"/>. Kept as a plain record so the controller's
// three endpoint methods stay one-liners that fill in the shape and hand it to the service.
internal sealed record RestoreRequest(
    RestoreKind Kind,
    long? HistoryId,
    string? ManifestEntryPath,
    bool Force);
