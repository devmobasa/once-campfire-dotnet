using System.Globalization;
using Campfire.Web.Http;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.Http;

/// <summary>
/// Response building blocks, ported from Rust <c>crates/kit/src/response.rs</c> and
/// <c>exceptions.rs</c> tests at <c>3fe9f61</c>, plus the Rack body middleware.
/// </summary>
public sealed class ResponsePartsTests
{
    [Fact]
    public void Content_disposition_adds_an_ascii_filename_and_a_utf8_filename_star_like_rails( )
    {
        Assert.Equal( "inline", RequestContext.ContentDisposition( "inline", null ) );
        Assert.Equal( "attachment; filename=\"logo.png\"; filename*=UTF-8''logo.png", RequestContext.ContentDisposition( "attachment", "logo.png" ) );
        Assert.Equal(
            "inline; filename=\"resume 1.pdf\"; filename*=UTF-8''r%C3%A9sum%C3%A9%201.pdf",
            RequestContext.ContentDisposition( "inline", "résumé 1.pdf" ) );
        Assert.Equal(
            "inline; filename=\"%3F%3F%22x%22.txt\"; filename*=UTF-8''%E6%97%A5%E6%9C%AC%22x%22.txt",
            RequestContext.ContentDisposition( "inline", "日本\"x\".txt" ) );
    }

    [Fact]
    public void Byte_range_parsing_clamps_a_single_range_and_ignores_multiple_or_non_byte_ranges( )
    {
        Assert.Equal( new SingleByteRange.Range( 0, 9 ), SingleByteRange.Parse( "bytes=0-9", 100 ) );
        Assert.Equal( new SingleByteRange.Range( 90, 99 ), SingleByteRange.Parse( "bytes=90-", 100 ) );
        Assert.Equal( new SingleByteRange.Range( 90, 99 ), SingleByteRange.Parse( "bytes=-10", 100 ) );
        Assert.Equal( new SingleByteRange.Range( 95, 99 ), SingleByteRange.Parse( "bytes=95-200", 100 ) );
        Assert.IsType<SingleByteRange.Unsatisfiable>( SingleByteRange.Parse( "bytes=100-", 100 ) );
        Assert.IsType<SingleByteRange.Ignore>( SingleByteRange.Parse( "bytes=0-1,5-6", 100 ) );
        Assert.IsType<SingleByteRange.Ignore>( SingleByteRange.Parse( "items=0-1", 100 ) );
    }

    [Fact]
    public void Cache_control_directives_are_normalized_in_rails_order( )
    {
        var staleWhileRevalidate = new CacheControl { MaxAge = 300, Public = true, StaleWhileRevalidate = 604800 };
        var revalidate = new CacheControl { MaxAge = 0, MustRevalidate = true };
        var noStore = new CacheControl { NoStore = true, MaxAge = 5 };

        Assert.Equal( "max-age=300, public, stale-while-revalidate=604800", CacheControl.Normalize( null, staleWhileRevalidate ) );
        Assert.Equal( "max-age=0, private, must-revalidate", CacheControl.Normalize( null, revalidate ) );
        Assert.Equal( "no-store", CacheControl.Normalize( null, noStore ) );
        Assert.Null( CacheControl.Normalize( null, new CacheControl( ) ) );
    }

    [Fact]
    public void An_explicit_cache_control_header_is_renormalized_and_merged( )
    {
        Assert.Equal( "max-age=60, public", CacheControl.Normalize( "public, max-age=60", new CacheControl( ) ) );
        Assert.Equal( "max-age=300, private, stale-if-error=5", CacheControl.Normalize( "no-cache, stale-if-error=5", new CacheControl { MaxAge = 300 } ) );
        Assert.Equal( "max-age=10, private", CacheControl.Normalize( "public", new CacheControl { MaxAge = 10, Public = null } ) );
    }

    [Fact]
    public void Rack_etag_digests_bytes_bodies_of_200_and_201_only( )
    {
        var ok = FrontendResult.WithBody( 200, "text/html", "hello" );
        var created = FrontendResult.WithBody( 201, "text/html", "hello" );
        var empty = FrontendResult.WithBody( 200, "text/html", "" );
        var notFound = FrontendResult.WithBody( 404, "text/html", "hello" );
        var live = FrontendResult.WithBody( 200, "text/html", "hello" );
        live.Live = true;

        foreach ( var response in ( FrontendResult[] ) [ok, created, empty, notFound, live] )
        {
            RackETag.Apply( response );
        }

        // SHA-256("hello"), truncated to 32 hex digits.
        Assert.Equal( "W/\"2cf24dba5fb0a30e26e83b2ac5b9e29e\"", ok.Header( "ETag" ) );
        Assert.Equal( "max-age=0, private, must-revalidate", ok.Header( "Cache-Control" ) );
        Assert.Equal( ok.Header( "ETag" ), created.Header( "ETag" ) );
        Assert.Null( empty.Header( "ETag" ) );
        Assert.Equal( "no-cache", empty.Header( "Cache-Control" ) );
        Assert.Null( notFound.Header( "ETag" ) );
        Assert.Null( live.Header( "ETag" ) );
        Assert.Equal( "no-cache", live.Header( "Cache-Control" ) );
    }

    [Fact]
    public void Conditional_get_matches_etag_lists_then_if_modified_since_and_releases_the_body( )
    {
        var headers = new HeaderDictionary { ["If-None-Match"] = "W/\"a\", W/\"b\"" };
        var response = FrontendResult.WithBody( 200, "text/html", "x" ).Header( "ETag", "W/\"a\"" );
        RackConditionalGet.Apply( "GET", headers, response );
        Assert.Equal( 304, response.Status );
        Assert.Equal( 0, response.Body.Length );

        var since = new HeaderDictionary { ["If-Modified-Since"] = "Sat, 01 Jun 2024 12:00:00 GMT" };
        var modified = FrontendResult.WithBody( 200, "text/html", "x" ).Header( "Last-Modified", "Sat, 01 Jun 2024 11:00:00 GMT" );
        RackConditionalGet.Apply( "HEAD", since, modified );
        Assert.Equal( 304, modified.Status );
        Assert.Null( modified.Header( "Content-Type" ) );

        var post = FrontendResult.WithBody( 200, "text/html", "x" ).Header( "Last-Modified", "Sat, 01 Jun 2024 11:00:00 GMT" );
        RackConditionalGet.Apply( "POST", since, post );
        Assert.Equal( 200, post.Status );
    }

    [Fact]
    public void Http_dates_format_as_imf_fixdate_and_parse_with_offsets_or_return_null( )
    {
        Assert.Equal( "Thu, 01 Jan 1970 00:00:00 GMT", HttpDate.Format( DateTimeOffset.UnixEpoch ) );
        Assert.Equal( DateTimeOffset.UnixEpoch, HttpDate.Parse( "Thu, 01 Jan 1970 00:00:00 GMT" ) );
        Assert.Equal( DateTimeOffset.Parse( "2024-06-01T10:00:00Z", CultureInfo.InvariantCulture ), HttpDate.Parse( "Sat, 01 Jun 2024 12:00:00 +0200" ) );
        Assert.Null( HttpDate.Parse( "garbage" ) );
    }
}
