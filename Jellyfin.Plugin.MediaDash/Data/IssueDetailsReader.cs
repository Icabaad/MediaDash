using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Boundary reader for the two <c>Issue.DetailsJson</c> payload shapes that drive destructive
/// fixer decisions today. Fields and keys are preserved verbatim (see the per-record docs) —
/// there is no DB migration and no JSON rewrite. Every method returns <c>false</c> for null,
/// empty, non-object, wrong-typed, or otherwise unparseable input; a returning
/// <c>true</c> means the payload met the schema, not that any path inside it is safe to act
/// on. Callers must still route sidecar paths through <c>LibraryGuard</c> and reject
/// self-referential entries.
/// </summary>
internal static class IssueDetailsReader
{
    /// <summary>Read a <see cref="PlayabilityDetails"/> from a persisted <c>DetailsJson</c>
    /// blob written by <see cref="Jellyfin.Plugin.MediaDash.Scanners.PlayabilityScanner"/>.
    /// A present-but-wrong-typed <c>reason</c> or <c>rename_to_ext</c> rejects the whole
    /// payload — the fixer must never see a partially-parsed record that a malformed row
    /// could steer.</summary>
    /// <param name="json">The raw JSON blob (nullable/empty is refused).</param>
    /// <param name="details">The parsed record on success; null on refusal.</param>
    /// <returns>True when the payload has an object root AND every present field is
    /// well-typed. False when the JSON is null/empty/malformed/non-object OR any present
    /// field has the wrong type (including JSON <c>null</c> for a string field — which is
    /// distinct from an absent field and is refused).</returns>
    internal static bool TryReadPlayability(string? json, out PlayabilityDetails? details)
    {
        details = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryReadStringField(doc.RootElement, "reason", out var reason)
                || !TryReadStringField(doc.RootElement, "rename_to_ext", out var renameToExt))
            {
                return false;
            }

            details = new PlayabilityDetails(reason, renameToExt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Read a <see cref="SubtitleCleanupDetails"/> from a persisted <c>DetailsJson</c>
    /// blob written by <see cref="Jellyfin.Plugin.MediaDash.Scanners.SubtitleLanguageScanner"/>.
    /// The <c>externalFiles</c> field is required as an array of strings — a scanner payload
    /// with the field missing or wrong-typed is rejected so the fixer can't be steered into
    /// a "no externals" execution branch by a garbled row.</summary>
    /// <param name="json">The raw JSON blob (nullable/empty is refused).</param>
    /// <param name="details">The parsed record on success; null on refusal.</param>
    /// <returns>True on a well-shaped object with a string-array <c>externalFiles</c>. Empty
    /// array is valid (no sidecars). False for null/empty/malformed/non-object/missing
    /// or wrong-typed <c>externalFiles</c>.</returns>
    internal static bool TryReadSubtitleCleanup(string? json, out SubtitleCleanupDetails? details)
    {
        details = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("externalFiles", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var list = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    // A single wrong-typed entry rejects the whole array — the fixer must
                    // never operate on a partially-parsed set of paths.
                    return false;
                }

                var path = el.GetString();
                if (!string.IsNullOrEmpty(path))
                {
                    list.Add(path);
                }
            }

            details = new SubtitleCleanupDetails(list);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Read a nullable-string field. Returns true (with <paramref name="value"/> = null)
    /// when the property is absent. Returns true (with the string value) when present and typed
    /// as a JSON string. Returns false when present but the value kind is anything else — a
    /// wrong-typed field is a schema violation that rejects the whole payload above.</summary>
    private static bool TryReadStringField(JsonElement root, string property, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(property, out var el))
        {
            return true;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = el.GetString();
        return true;
    }
}
