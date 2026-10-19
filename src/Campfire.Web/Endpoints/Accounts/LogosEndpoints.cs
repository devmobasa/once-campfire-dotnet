using Campfire.Application.Accounts;
using Campfire.Application.Data;
using Campfire.Application.Storage;
using Campfire.Web.App;
using Campfire.Web.Http;
using Campfire.Web.Paths;
using Campfire.Web.Presenters;
using Campfire.Web.Views.Caching;

namespace Campfire.Web.Endpoints.Accounts;

/// <summary>
/// <c>Accounts::LogosController</c> (<c>app/controllers/accounts/logos_controller.rb</c>): the account logo (or the
/// stock app icon) as a PNG, public so it can be the PWA icon. It includes <c>ActiveStorage::Streaming</c>, so
/// responses are live. <c>destroy</c> removes the logo.
/// </summary>
public static partial class LogosEndpoints
{
    /// <summary><c>expires_in 5.minutes</c>, in seconds.</summary>
    private const long MaxAgeSeconds = 5 * 60;

    /// <summary><c>logo_size</c>'s <c>small</c>: the PWA's small icon size, in pixels.</summary>
    private const int SmallLogoSize = 192;

    /// <summary>The full logo size, in pixels.</summary>
    private const int LogoSize = 512;

    /// <summary>
    /// <c>Current.account.logo.destroy</c> (its blob purged after commit), then <c>redirect_to edit_account_url</c>. The
    /// controller's <c>ActionController::Live</c> covers this action too.
    /// </summary>
    private static async Task<FrontendResult> DestroyAsync( RequestContext requestContext )
    {
        var database = requestContext.Database( );
        await database.WriteAsync( RemoveLogo, requestContext.RequestAborted );

        var response = requestContext.RedirectTo( requestContext.UrlFor( Routes.EditAccount ) );
        response.Live = true;
        return response;
    }

    private static void RemoveLogo( WriteScope scope ) =>
        AccountOperations.RemoveLogo( scope, CurrentAccount.Require( scope.Db, "logo" ).Id );

    /// <summary>
    /// <c>stale?(etag: Current.account)</c>, <c>expires_in 5.minutes, public: true, stale_while_revalidate: 1.week</c>,
    /// then <c>Current.account&amp;.logo_variant(logo_size)</c> or the stock icon.
    /// </summary>
    private static async Task<FrontendResult> ShowAsync( RequestContext requestContext )
    {
        var response = await RespondAsync( requestContext );
        response.Live = true;
        return response;
    }

    private static async Task<FrontendResult> RespondAsync( RequestContext requestContext )
    {
        var app = requestContext.App( );
        var account = await app.Database.ReadAsync( AccountQueries.First, requestContext.RequestAborted );

        // There's no accounts/logos/show template to digest.
        var freshness = new Freshness { Etag = account is null ? null : [AccountCacheKey( account )] };
        if ( requestContext.FreshWhen( freshness ) is { } notModified )
        {
            return notModified;
        }

        requestContext.ExpiresIn(
            MaxAgeSeconds,
            new ExpiresInOptions { Public = true, StaleWhileRevalidate = PublicImageCaching.StaleWhileRevalidateSeconds } );

        var small = requestContext.Param( ParamNames.Accounts.LogoSize ) == "small";
        if ( account is not null )
        {
            var size = small ? SmallLogoSize : LogoSize;
            var owner = new AttachmentOwner( ModelNames.Account, account.Id );
            var resize = Variation.ResizeToLimit( size, size, "png" );
            var variant = await AttachedImages.ProcessedVariantAsync( app, owner, AccountOperations.LogoName, resize, requestContext.RequestAborted );
            if ( variant is not null )
            {
                return requestContext.SendFile( app.Storage.Disk.PathFor( variant.Key ), SendOptions.Inline( ContentTypes.Png ) );
            }
        }

        var referenceFiles = requestContext.HttpContext.RequestServices.GetRequiredService<ReferenceFiles>( );
        var stock = referenceFiles.Image( Path.Combine( "logos", small ? "app-icon-192.png" : "app-icon.png" ) );
        return requestContext.SendFile( stock, SendOptions.Inline( ContentTypes.Png ) );
    }

    /// <summary><c>Current.account</c> as an ETag: its cache key, which changes with <c>updated_at</c>.</summary>
    private static string AccountCacheKey( Account account ) =>
        new RecordVersion( account.Id, UserPresentation.AsUtc( account.UpdatedAt ) ).CacheKey( TableNames.Accounts );
}
