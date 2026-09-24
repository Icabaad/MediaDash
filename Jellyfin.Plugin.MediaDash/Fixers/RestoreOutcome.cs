namespace Jellyfin.Plugin.MediaDash.Fixers;

// The result of a <see cref="RestoreService"/> call. The controller layer maps this to an HTTP
// response.
//
// Mapping (05-B controller adapter):
// * Success=true                                  → 200 OK with { RestoredTo, Suffixed }
// * Success=false, IsNotFound=true                → 404
// * Success=false, otherwise                      → 409 with TechnicalDetail as the body
//
// Reason is a typed FixReason so future observability (Errors tab, metrics, tests) can classify
// without parsing the TechnicalDetail string. RestoredTo / Suffixed are only meaningful when
// Success=true; consumers must not read them otherwise.
internal sealed record RestoreOutcome(
    bool Success,
    FixReason Reason,
    string? RestoredTo,
    bool Suffixed,
    string? TechnicalDetail,
    bool IsNotFound = false);
