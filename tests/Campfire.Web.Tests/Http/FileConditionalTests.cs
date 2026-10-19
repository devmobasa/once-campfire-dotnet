using Campfire.Web.Http;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.Http;

public sealed class FileConditionalTests
{
    [Theory]
    [InlineData( "file", "GET" )]
    [InlineData( "file", "HEAD" )]
    [InlineData( "data", "GET" )]
    [InlineData( "data", "HEAD" )]
    public async Task Send_file_and_data_apply_the_shared_validators_before_range_selection( string kind, string method )
    {
        var path = Path.GetTempFileName( );
        await File.WriteAllTextAsync( path, "0123456789", TestContext.Current.CancellationToken );
        try
        {
            foreach ( var range in new[] { "bytes=0-0", "bytes=999-", "bytes=invalid" } )
            {
                foreach ( var validator in new[] { "wildcard", "list", "date" } )
                {
                    var context = await TestContexts.CreateAsync( method, configure: request =>
                    {
                        request.Headers.Range = range;
                        request.Headers[validator == "date" ? "If-Modified-Since" : "If-None-Match"] = validator switch
                        {
                            "wildcard" => "*",
                            "list" => "\"other\", \"current\"",
                            _ => "Sat, 01 Jun 2024 12:01:00 GMT",
                        };
                    } );
                    context.ResponseHeaders.ETag = "W/\"current\"";
                    context.ResponseHeaders.LastModified = "Sat, 01 Jun 2024 12:00:00 GMT";
                    var options = new SendOptions { Ranges = true, ContentType = "text/plain" };
                    var response = kind == "file" ? context.SendFile( path, options ) : context.SendData( "0123456789"u8.ToArray( ), options );

                    Assert.Equal( StatusCodes.Status304NotModified, response.Status );
                    Assert.Equal( 0, response.Body.Length );
                    Assert.Null( response.ContentType );
                    Assert.Null( response.Header( "Content-Range" ) );
                }
            }
        }
        finally
        {
            File.Delete( path );
        }
    }

    [Theory]
    [InlineData( "GET" )]
    [InlineData( "HEAD" )]
    public async Task Send_data_ranges_revalidate_with_the_full_body_validator( string method )
    {
        var first = await TestContexts.CreateAsync( );
        var options = new SendOptions { Ranges = true, ContentType = "text/plain" };
        var original = first.SendData( "0123456789"u8.ToArray( ), options );
        await original.ExecuteAsync( first.HttpContext );
        var etag = original.Header( "ETag" );
        Assert.NotNull( etag );
        var next = await TestContexts.CreateAsync( method, configure: request =>
        {
            request.Headers.Range = "bytes=0-0";
            request.Headers.IfNoneMatch = "\"other\", " + etag;
        } );

        var response = next.SendData( "0123456789"u8.ToArray( ), options );

        Assert.Equal( StatusCodes.Status304NotModified, response.Status );
        Assert.Equal( 0, response.Body.Length );
        Assert.Null( response.ContentType );
    }

    [Fact]
    public async Task Conditional_file_headers_do_not_convert_a_post_response( )
    {
        var context = await TestContexts.CreateAsync( "POST", configure: request =>
        {
            request.Headers.IfNoneMatch = "*";
        } );

        var response = context.SendData( "0123456789"u8.ToArray( ), new SendOptions { Ranges = true } );

        Assert.Equal( StatusCodes.Status200OK, response.Status );
        Assert.Equal( 10, response.Body.Length );
    }

    [Fact]
    public async Task A_missing_send_file_is_not_revalidated_as_an_existing_representation( )
    {
        var context = await TestContexts.CreateAsync( configure: request => request.Headers.IfNoneMatch = "*" );
        var path = Path.Combine( Path.GetTempPath( ), "missing-" + Guid.NewGuid( ).ToString( "N" ) );

        Assert.Throws<FileNotFoundException>( ( ) => context.SendFile( path, new SendOptions { Ranges = true } ) );
    }
}
