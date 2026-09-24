using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// F-015 sibling (bug-pattern audit round 2, 2026-09-19): `SmartHealthProbe.ParseSmartctlJson`
/// wraps only <see cref="System.Text.Json.JsonException"/>, but every `TryGetProperty` /
/// `GetString` call assumed the JSON root and each nested object was well-formed. Vendor
/// firmware quirks and future `--json` format changes have produced non-object root wrappers
/// and mixed-shape array elements in the wild. Before the guards landed, any such shape threw
/// `InvalidOperationException` from `TryGetProperty` on non-object / `GetString` on non-string,
/// which escaped the JsonException catch and 500'd the Overview storage-health card for the
/// rest of the session. These tests pin each malformed shape against the fail-closed guards.
/// </summary>
public class SmartHealthProbeParseSafetyTests
{
    [Theory]
    // Root not an object — the primary F-015 shape. Every downstream TryGetProperty would throw.
    [InlineData("[]")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void ParseSmartctlJson_NonObjectRoot_ReturnsUnknownWithoutThrowing(string json)
    {
        var result = SmartHealthProbe.ParseSmartctlJson(json, "/dev/sda");
        Assert.Equal(SmartHealth.Unknown, result.Status);
    }

    [Theory]
    // smartctl child is non-object → meta.TryGetProperty on non-object used to throw IOE.
    [InlineData("{\"smartctl\": null}")]
    [InlineData("{\"smartctl\": \"not an object\"}")]
    [InlineData("{\"smartctl\": [1, 2]}")]
    // smart_status child is non-object.
    [InlineData("{\"smart_status\": \"passed\"}")]
    [InlineData("{\"smart_status\": [true]}")]
    // ata_smart_attributes non-object.
    [InlineData("{\"ata_smart_attributes\": 5}")]
    public void ParseSmartctlJson_NonObjectChildren_DoNotThrow(string json)
    {
        // The pre-fix crash was `InvalidOperationException` from TryGetProperty on non-object
        // escaping the `catch (JsonException)`. The specific Status returned for malformed input
        // is a policy call (Unknown vs the fail-safe Failing fallback); this test only cares
        // that parsing completes and returns a real result rather than throwing.
        var result = SmartHealthProbe.ParseSmartctlJson(json, "/dev/sda");
        Assert.NotNull(result);
    }

    [Fact]
    public void ParseSmartctlJson_MessagesArrayWithMixedElements_DoesNotThrow()
    {
        // Firmware quirk (seen in the wild): messages is an array of plain strings instead of
        // the expected `[{"string": "..."}]` shape. m.TryGetProperty on a scalar element would
        // throw IOE before the audit fix.
        var json = "{" +
            "\"smartctl\": {" +
                "\"exit_status\": 1," +
                "\"messages\": [\"plain string one\", {\"string\": \"wrapped two\"}, 42, null]" +
            "}}";

        var result = SmartHealthProbe.ParseSmartctlJson(json, "/dev/sda");

        Assert.Equal(SmartHealth.Unknown, result.Status);
        Assert.Contains("wrapped two", result.Message);
    }

    [Fact]
    public void ParseSmartctlJson_AttributeTableEntryWithNonStringName_DoesNotThrow()
    {
        // Failing attribute row whose "name" is a number (rare vendor quirk) — GetString on
        // non-string used to throw IOE. Fallback label "attribute" is used now.
        var json = "{" +
            "\"smart_status\": {\"passed\": true}," +
            "\"ata_smart_attributes\": {\"table\": [" +
                "{\"name\": 42, \"when_failed\": \"failing_now\"}" +
            "]}}";

        var result = SmartHealthProbe.ParseSmartctlJson(json, "/dev/sda");

        Assert.Equal(SmartHealth.Warning, result.Status);
        Assert.Contains("attribute", result.Message);
    }

    [Fact]
    public void ParseSmartctlJson_HappyPath_HealthyDrive()
    {
        // Regression coverage: the guards must not break well-formed smartctl output.
        var json = "{" +
            "\"smart_status\": {\"passed\": true}," +
            "\"ata_smart_attributes\": {\"table\": [" +
                "{\"name\": \"Reallocated_Sector_Ct\", \"when_failed\": \"never\"}" +
            "]}}";

        var result = SmartHealthProbe.ParseSmartctlJson(json, "/dev/sda");

        Assert.Equal(SmartHealth.Healthy, result.Status);
    }

    [Fact]
    public void ParseSmartctlJson_HappyPath_FailingAttributeSurfaces()
    {
        var json = "{" +
            "\"smart_status\": {\"passed\": true}," +
            "\"ata_smart_attributes\": {\"table\": [" +
                "{\"name\": \"Reallocated_Sector_Ct\", \"when_failed\": \"failing_now\"}" +
            "]}}";

        var result = SmartHealthProbe.ParseSmartctlJson(json, "/dev/sda");

        Assert.Equal(SmartHealth.Warning, result.Status);
        Assert.Contains("Reallocated_Sector_Ct", result.Message);
    }
}
