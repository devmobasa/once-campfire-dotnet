using Campfire.Application.Storage.Http;
using Campfire.Web.App;
using Campfire.Web.Http;
using Campfire.Web.Paths;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Endpoints.ActiveStorage;

/// <summary>
/// <c>ActiveStorage::Blobs::ProxyController</c>: <c>include ActiveStorage::SetBlob, ActiveStorage::Streaming,
/// ActiveStorage::DisableSession</c>; the blob's bytes through the app, with ranges.
/// </summary>
public static partial class BlobsProxyEndpoints
{
    /// <summary>
    /// Checks file availability and validators before selecting byte ranges or streaming the whole blob.
    /// </summary>
    private static Task<FrontendResult> ShowAsync( RequestContext requestContext )
    {
        var blob = ActiveStorageBase.BlobOf( requestContext );
        var response = ActiveStorageBase.SendBlobStream( requestContext, blob, requestContext.Param( ParamNames.ActiveStorage.Disposition ) );
        response.Live = true;
        if ( response.Status != StatusCodes.Status200OK )
        {
            return Task.FromResult( response );
        }

        if ( ActiveStorageBase.HttpCacheForever( requestContext ) is { } notModified )
        {
            notModified.Live = true;
            return Task.FromResult( notModified );
        }

        if ( requestContext.Request.Header( HeaderNames.Range ) is { } range && !string.IsNullOrWhiteSpace( range ) )
        {
            var ranged = ActiveStorageBase.ToResponse( BlobStreaming.ByteRangeData( requestContext.App( ).Storage.Disk, blob, range ) );
            ranged.Live = true;
            return Task.FromResult( ranged );
        }

        response.Headers.AcceptRanges = "bytes";
        response.Headers.ContentLength = blob.ByteSize;
        return Task.FromResult( response );
    }
}
