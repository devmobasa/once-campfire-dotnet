using System.Globalization;
using System.Text.Json.Nodes;

namespace Campfire.Web.Tests.Assets;

/// <summary>
/// Three overlays from basecamp/once-campfire commit <c>05c5a2c0d72f7fd74d7c2ace23cc123000f956b9</c>.
/// The historical vectors stay unchanged. Independent SHA-1(content + "1.0") names and raw SHA-256 hashes pin the
/// current files, which contain no Propshaft asset or source-map rewrites.
/// </summary>
internal static class CurrentRailsAssetExpectations
{
    public static IReadOnlyList<CurrentRailsAsset> Overlays { get; } =
    [
        new(
            "controllers/read_rooms_controller.js",
            "controllers/read_rooms_controller-8245522a.js",
            "81b2ba832f6435b89f94bccce9bbc5b69ccb56245f50b0a1ac8b85f01e96c53b",
            573,
            "controllers/read_rooms_controller-a01de691.js",
            "9df29819041d919daaa2db8348b3553eb88905fd65dc433f152f22c5c716a4e9",
            581 ),
        new(
            "controllers/rooms_list_controller.js",
            "controllers/rooms_list_controller-64840fcd.js",
            "a9148a7a494d2d9f8429dfd1c11c5e6168fee1ff482a249b596ea363ea744efc",
            1565,
            "controllers/rooms_list_controller-988f5f6c.js",
            "87c5d6ca13810ed033ea56f1a328f06afa3736b978960ced24b692a57fe07488",
            3995 ),
        new(
            "lib/autocomplete/base_autocomplete_handler.js",
            "lib/autocomplete/base_autocomplete_handler-e29a896e.js",
            "81e34d36e1449fc1c69bb59646e9fa8d24b2544e6775907a04584a2b8d278ebb",
            3311,
            "lib/autocomplete/base_autocomplete_handler-96e7e419.js",
            "cea202361b483970f4cfbb6891e429364ee1cff450bc9fb22b6b43197443e553",
            3340 ),
    ];

    public static JsonObject Manifest( )
    {
        var expected = AssetFixture.JsonVector( "manifest.json" ).AsObject( );
        foreach ( var overlay in Overlays )
        {
            var entry = expected[overlay.LogicalPath]!.AsObject( );
            Assert.Equal( overlay.HistoricalDigestedPath, entry["digested_path"]!.GetValue<string>( ) );
            entry["digested_path"] = overlay.DigestedPath;
        }

        return expected;
    }

    public static JsonObject CompiledHashes( )
    {
        var expected = AssetFixture.JsonVector( "compiled_sha256.json" ).AsObject( );
        foreach ( var overlay in Overlays )
        {
            Assert.Equal( overlay.HistoricalSha256, expected[overlay.HistoricalDigestedPath]!.GetValue<string>( ) );
            Assert.False( expected.ContainsKey( overlay.DigestedPath ) );
            Assert.True( expected.Remove( overlay.HistoricalDigestedPath ) );
            expected.Add( overlay.DigestedPath, overlay.Sha256 );
        }

        return expected;
    }

    public static string ImportmapTags( )
    {
        var expected = AssetFixture.Vector( "javascript_importmap_tags.html" );
        foreach ( var overlay in Overlays )
        {
            var historicalUrl = "/assets/" + overlay.HistoricalDigestedPath;
            Assert.Equal( 2, expected.Split( historicalUrl, StringSplitOptions.None ).Length - 1 );
            Assert.DoesNotContain( "/assets/" + overlay.DigestedPath, expected, StringComparison.Ordinal );
            expected = expected.Replace( historicalUrl, "/assets/" + overlay.DigestedPath, StringComparison.Ordinal );
        }

        return expected;
    }

    public static JsonArray StaticCases( )
    {
        var expected = AssetFixture.JsonVector( "static_responses.json" ).AsArray( );
        foreach ( var overlay in Overlays )
        {
            var entry = Assert.Single( expected,
                entry => entry!["path"]!.GetValue<string>( ) == "/assets/" + overlay.HistoricalDigestedPath )!;
            Assert.Equal( "GET", entry["method"]!.GetValue<string>( ) );
            Assert.Equal( 200, entry["status"]!.GetValue<int>( ) );
            Assert.Equal( overlay.HistoricalSha256, entry["body_sha256"]!.GetValue<string>( ) );
            Assert.Equal(
                overlay.HistoricalByteLength.ToString( CultureInfo.InvariantCulture ),
                entry["headers"]!["content-length"]!.GetValue<string>( ) );
            entry["path"] = "/assets/" + overlay.DigestedPath;
            entry["body_sha256"] = overlay.Sha256;
            entry["headers"]!["content-length"] = overlay.ByteLength.ToString( CultureInfo.InvariantCulture );
        }

        return expected;
    }
}

internal sealed record CurrentRailsAsset(
    string LogicalPath,
    string HistoricalDigestedPath,
    string HistoricalSha256,
    int HistoricalByteLength,
    string DigestedPath,
    string Sha256,
    int ByteLength );
