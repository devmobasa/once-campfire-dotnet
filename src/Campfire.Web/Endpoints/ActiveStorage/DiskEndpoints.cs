using Campfire.Application.Storage.Http;
using Campfire.Web.App;
using Campfire.Web.Http;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Endpoints.ActiveStorage;

/// <summary>
/// <c>ActiveStorage::DiskController</c> (<c>include ActiveStorage::FileServer</c>, <c>skip_forgery_protection</c>): serves
/// a file by its signed, expiring key, with ranges and conditional GET, and receives a direct upload's bytes by its
/// signed token. Campfire's initializers add <c>Cache-Control: max-age=3600, public</c> after <c>show</c>
/// (<c>config/initializers/active_storage.rb</c>) and require a session for <c>update</c>
/// (<c>config/initializers/active_storage_authentication.rb</c>).
/// </summary>
public static partial class DiskEndpoints
{
    /// <summary><c>serve_file</c> for a verified key; <c>head :not_found</c> for a bad key or a missing file.</summary>
    private static Task<FrontendResult> ShowAsync( RequestContext requestContext, string encodedKey )
    {
        var app = requestContext.App( );
        var request = new FileRequest(
            requestContext.Request.Method,
            requestContext.Request.Header( HeaderNames.Range ),
            modified => RackConditionalGet.IsFresh( requestContext.Request.Headers, etag: null, modified ) );
        var served = FileServer.DiskShow( app.Storage.Disk, app.StorageVerifier, encodedKey, request, app.Now );

        var result = ActiveStorageBase.ToResponse( served );
        result.Headers.CacheControl = FileServer.DiskCacheControl;
        return Task.FromResult( result );
    }
}
