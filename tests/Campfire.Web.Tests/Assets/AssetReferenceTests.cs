using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Web.Assets;

namespace Campfire.Web.Tests.Assets;

/// <summary>
/// Golden tests against what the Rails app produced: <c>vectors/assets/*</c>, written by
/// <c>reference-tools/assets</c> from <c>assets:precompile</c> and the real Rails helpers. Ported
/// from Rust <c>crates/assets/tests/reference.rs</c>. Three current Rails overlays have explicit expectations.
/// </summary>
public sealed class AssetReferenceTests( AssetFixture fixture ) : IClassFixture<AssetFixture>
{
    [Fact]
    public void Manifest_matches_the_reference_precompile( )
    {
        var reference = CurrentRailsAssetExpectations.Manifest( )
            .ToDictionary( entry => entry.Key, entry => entry.Value!["digested_path"]!.GetValue<string>( ), StringComparer.Ordinal );
        var ours = fixture.Catalog.Manifest.ToDictionary( StringComparer.Ordinal );

        var differing = reference
            .Where( entry => !ours.TryGetValue( entry.Key, out var digested ) || digested != entry.Value )
            .Select( entry => entry.Key )
            .ToList( );
        var extra = ours.Keys.Where( logical => !reference.ContainsKey( logical ) ).ToList( );
        Assert.True(
            differing.Count == 0 && extra.Count == 0,
            $"differs from the reference: [{string.Join( ", ", differing )}], extra: [{string.Join( ", ", extra )}]" );
        Assert.Equal( 314, ours.Count );

        // The served manifest has the same entries; only the order (readdir vs. load path) can differ.
        var served = JsonNode.Parse( fixture.Catalog.ManifestJson )!.AsObject( );
        var referenceJson = CurrentRailsAssetExpectations.Manifest( );
        Assert.Equal( referenceJson.Count, served.Count );
        foreach ( var (logical, entry) in referenceJson )
        {
            Assert.True( JsonNode.DeepEquals( entry, served[logical] ), $"manifest entry {logical}" );
        }
    }

    [Fact]
    public void Compiled_files_are_byte_identical_to_the_reference_precompile( )
    {
        var reference = CurrentRailsAssetExpectations.CompiledHashes( );
        var mismatched = new List<string>( );
        foreach ( var (digestedPath, expected) in reference )
        {
            var response = fixture.Get( $"/assets/{digestedPath}" );
            if ( Sha256( response.Body ) != expected!.GetValue<string>( ) )
            {
                mismatched.Add( digestedPath );
            }
        }

        Assert.True( mismatched.Count == 0, $"compiled output differs for [{string.Join( ", ", mismatched )}]" );
        Assert.Equal( reference.Count, fixture.Catalog.Manifest.Count( ) );
    }

    [Fact]
    public void Stylesheet_link_tag_all_and_its_link_header_match_the_reference( )
    {
        var tags = fixture.Tags.StylesheetLinkTagAll( new KeyValuePair<string, string>( "data-turbo-track", "reload" ) );

        Assert.Equal( AssetFixture.Vector( "stylesheet_link_tag_all.html" ), tags.Html );
        Assert.Equal( AssetFixture.Vector( "link_header.txt" ), AssetTags.AppendPreloadLinks( string.Empty, tags.PreloadLinks ) );
    }

    [Fact]
    public void Javascript_importmap_tags_match_the_reference( ) =>
        Assert.Equal( CurrentRailsAssetExpectations.ImportmapTags( ), fixture.Tags.JavascriptImportmapTags( ) );

    [Fact]
    public void Current_rails_overlays_take_precedence_over_the_pinned_reference_assets( )
    {
        foreach ( var overlay in CurrentRailsAssetExpectations.Overlays )
        {
            Assert.Equal( overlay.DigestedPath, fixture.Catalog.DigestedPath( overlay.LogicalPath ) );
        }
    }

    [Fact]
    public void Public_and_asset_files_are_served_like_action_dispatch_static( )
    {
        var cases = CurrentRailsAssetExpectations.StaticCases( );
        Assert.NotEmpty( cases );

        foreach ( var testCase in cases )
        {
            var environment = testCase!["env"]!.AsObject( );
            var request = new StaticRequest(
                testCase["method"]!.GetValue<string>( ),
                testCase["path"]!.GetValue<string>( ),
                AcceptEncoding: environment["HTTP_ACCEPT_ENCODING"]?.GetValue<string>( ),
                Range: environment["HTTP_RANGE"]?.GetValue<string>( ) );
            AssertStaticCase( request, testCase, $"{request.Method} {request.RawPath} {environment.ToJsonString( )}" );
        }
    }

    private void AssertStaticCase( StaticRequest request, JsonNode testCase, string label )
    {
        var expectedStatus = testCase["status"]!.GetValue<int>( );
        var expectedHeaders = testCase["headers"]!.AsObject( ).ToDictionary(
            header => header.Key,
            header => header.Value!.GetValue<string>( ),
            StringComparer.Ordinal );
        var response = fixture.StaticFiles.Serve( request );

        // The probe's fallthrough app answers 404 with x-cascade: pass.
        if ( expectedStatus == 404 && expectedHeaders.ContainsKey( "x-cascade" ) )
        {
            Assert.True( response is null, $"{label} should fall through" );
            return;
        }

        Assert.True( response is not null, $"{label} isn't served" );
        Assert.Equal( expectedStatus, response.Status );

        var ours = response.Headers.Where( header => header.Key != "last-modified" ).ToDictionary( StringComparer.Ordinal );

        // Our manifest lists the same entries in load-path order rather than the build machine's
        // readdir order, so its length and bytes can't match.
        if ( request.RawPath == "/assets/.manifest.json" )
        {
            Assert.Equal( expectedHeaders["content-type"], ours["content-type"] );
            return;
        }

        Assert.Equal( expectedHeaders.OrderBy( header => header.Key ), ours.OrderBy( header => header.Key ) );
        if ( request.Method == "GET" )
        {
            Assert.True( testCase["body_sha256"]!.GetValue<string>( ) == Sha256( response.Body ), $"{label} body" );
        }
    }

    [Fact]
    public void Last_modified_round_trips_to_a_304( )
    {
        foreach ( var path in new[] { "/robots.txt", fixture.Paths.AudioPath( "56k.mp3" ) } )
        {
            var lastModified = fixture.Get( path ).Header( "last-modified" );
            Assert.NotNull( lastModified );

            var notModified = fixture.StaticFiles.Serve( new StaticRequest( "GET", path, IfModifiedSince: lastModified ) );

            Assert.NotNull( notModified );
            Assert.Equal( 304, notModified.Status );
            Assert.Empty( notModified.Headers );
            Assert.True( notModified.Body.IsEmpty );
        }
    }

    [Fact]
    public void Head_requests_have_headers_but_no_body( )
    {
        var response = fixture.StaticFiles.Serve( new StaticRequest( "HEAD", "/robots.txt" ) );

        Assert.NotNull( response );
        Assert.Equal( 200, response.Status );
        Assert.True( response.Body.IsEmpty );
        Assert.Equal( "99", response.Header( "content-length" ) );
    }

    [Fact]
    public void Multiple_ranges_are_multipart_with_the_files_own_content_type( )
    {
        var response = fixture.StaticFiles.Serve( new StaticRequest( "GET", fixture.Paths.AudioPath( "56k.mp3" ), Range: "bytes=0-1, 4-5" ) );

        Assert.NotNull( response );
        Assert.Equal( 206, response.Status );
        // Rack sets multipart/byteranges, then Static overwrites it with the file's type.
        Assert.Equal( "audio/mpeg", response.Header( "content-type" ) );
        var body = Encoding.Latin1.GetString( response.Body.Span );
        Assert.StartsWith( "\r\n--AaB03x\r\ncontent-type: audio/mpeg\r\ncontent-range: bytes 0-1/", body, StringComparison.Ordinal );
        Assert.EndsWith( "\r\n--AaB03x--\r\n", body, StringComparison.Ordinal );
        Assert.Equal( body.Length.ToString( CultureInfo.InvariantCulture ), response.Header( "content-length" ) );
    }

    private static string Sha256( ReadOnlyMemory<byte> bytes ) => Convert.ToHexStringLower( SHA256.HashData( bytes.Span ) );
}
