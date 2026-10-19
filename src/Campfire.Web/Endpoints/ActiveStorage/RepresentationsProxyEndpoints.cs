using Campfire.Web.Http;
using Campfire.Web.Paths;

namespace Campfire.Web.Endpoints.ActiveStorage;

/// <summary>
/// <c>ActiveStorage::Representations::ProxyController</c>: <c>include ActiveStorage::Streaming,
/// ActiveStorage::DisableSession</c>; the processed representation's bytes through the app.
/// </summary>
public static partial class RepresentationsProxyEndpoints
{
    /// <summary>
    /// Checks processed-file availability before conditional validation, then streams a changed representation.
    /// </summary>
    private static Task<FrontendResult> ShowAsync( RequestContext requestContext )
    {
        var image = RepresentationsRedirectEndpoints.RepresentationOf( requestContext );
        var response = ActiveStorageBase.SendBlobStream( requestContext, image, requestContext.Param( ParamNames.ActiveStorage.Disposition ) );
        if ( response.Status == StatusCodes.Status200OK )
        {
            response = ActiveStorageBase.HttpCacheForever( requestContext ) ?? response;
        }

        response.Live = true;
        return Task.FromResult( response );
    }
}
