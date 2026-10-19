using System.Net.Mime;
using Campfire.Application.Data;
using Campfire.Application.Security;
using Campfire.Application.Storage;
using Campfire.Application.Users;
using Campfire.Web.App;
using Campfire.Web.Http;
using Campfire.Web.Paths;
using Campfire.Web.Policy;
using Campfire.Web.Presenters;
using Campfire.Web.Security;
using Campfire.Web.Views.Caching;
using Campfire.Web.Views.Models;
using Campfire.Web.Views.Rendering;

namespace Campfire.Web.Endpoints.Users;

/// <summary>
/// <c>Users::AvatarsController</c> (<c>app/controllers/users/avatars_controller.rb</c>): a user's avatar by its signed
/// avatar token: the uploaded image's <c>:square</c> variant, the default bot avatar, or an SVG of the initials. It
/// includes <c>ActiveStorage::Streaming</c>, so responses are live.
/// </summary>
public static partial class AvatarsEndpoints
{
    /// <summary>
    /// The digest <c>EtagWithTemplateDigest</c> adds when <c>users/avatars/show.svg.erb</c> is found for the request's
    /// formats. Only its presence shows; the value is the Rust port's reading of the reference.
    /// </summary>
    public const string ShowTemplateDigest = "d500db55e2a67222018ef0156839c3c9";

    /// <summary><c>expires_in 30.minutes</c>, in seconds: how long a shared avatar link may be cached.</summary>
    private const long MaxAgeSeconds = 30 * 60;

    /// <summary><c>attachable.variant :square, resize_to_limit: [ 512, 512 ], format: :webp</c>.</summary>
    public static Variation Square { get; } = Variation.ResizeToLimit( 512, 512, "webp" );

    /// <summary>
    /// <c>Current.user.avatar.destroy</c> (its blob purged after commit, the user touched), then
    /// <c>redirect_to user_profile_url</c>: the route's <c>user_id</c> is ignored, so nobody can remove another user's
    /// avatar. The controller's <c>ActionController::Live</c> covers this action too.
    /// </summary>
    private static async Task<FrontendResult> DestroyAsync( RequestContext requestContext )
    {
        var user = requestContext.RequireCurrentUserRecord( UndefinedMethodErrors.AvatarForNil );
        var database = requestContext.Database( );
        await database.WriteAsync( scope => UserAvatars.Remove( scope, user.Id ), requestContext.RequestAborted );

        var response = requestContext.RedirectTo( requestContext.UrlFor( Routes.UserProfile ) );
        response.Live = true;
        return response;
    }

    /// <summary>
    /// <c>User.from_avatar_token(params[:user_id])</c> (a bad signature is <c>head :not_found</c>, a missing user
    /// <c>RecordNotFound</c>); then <c>stale?(etag: @user)</c>, <c>expires_in 30.minutes, public: true,
    /// stale_while_revalidate: 1.week</c>, and the image.
    /// </summary>
    private static async Task<FrontendResult> ShowAsync( RequestContext requestContext, string signedUserId )
    {
        var response = await RespondAsync( requestContext, signedUserId );
        response.Live = true;
        return response;
    }

    private static async Task<FrontendResult> RespondAsync( RequestContext requestContext, string signedUserId )
    {
        var app = requestContext.App( );
        var now = app.Now;
        var verified = app.SignedIds.Verify( ModelNames.User, signedUserId, SignedIds.AvatarPurpose, now, out var expiresAt );
        if ( verified is null )
        {
            verified = requestContext.HttpContext.RequestServices.GetRequiredService<RailsAvatarTokens>( ).Verify( signedUserId, now, out expiresAt );
        }

        if ( verified is not { } userId )
        {
            return requestContext.Head( StatusCodes.Status404NotFound );
        }

        var user = await app.Database.ReadAsync( db => UserQueries.FindById( db, userId ), requestContext.RequestAborted )
            ?? throw new RecordNotFoundException( ModelNames.User );

        var freshness = new Freshness
        {
            Etag = [new RecordVersion( user.Id, UserPresentation.AsUtc( user.UpdatedAt ) ).CacheKey( TableNames.Users )],
            TemplateDigest = SvgTemplateFound( requestContext ) ? ShowTemplateDigest : null,
        };
        // Expiring links must be checked on every shared-cache request, including 304s.
        var expiryOptions = expiresAt is null
            ? new ExpiresInOptions { Public = true, StaleWhileRevalidate = PublicImageCaching.StaleWhileRevalidateSeconds }
            : new ExpiresInOptions { Public = false };
        var maxAge = expiresAt is { } expiry ? Math.Min( MaxAgeSeconds, ( long ) (expiry - now).TotalSeconds ) : MaxAgeSeconds;
        requestContext.ExpiresIn( maxAge, expiryOptions );
        if ( requestContext.FreshWhen( freshness ) is { } notModified )
        {
            return notModified;
        }

        var owner = new AttachmentOwner( ModelNames.User, user.Id );
        var variant = await AttachedImages.ProcessedVariantAsync( app, owner, UserAvatars.AttachmentName, Square, requestContext.RequestAborted );
        if ( variant is not null )
        {
            return requestContext.SendFile( app.Storage.Disk.PathFor( variant.Key ), SendOptions.Inline( MediaTypeNames.Image.Webp ) );
        }

        if ( user.IsBot )
        {
            var path = requestContext.HttpContext.RequestServices.GetRequiredService<ReferenceFiles>( ).Image( "default-bot-avatar.svg" );
            return requestContext.SendFile( path, SendOptions.Inline( ContentTypes.Svg ) );
        }

        // render formats: :svg
        var svg = await SliceRenderer.RenderToStringAsync(
            Views.users.avatars.show_svg.Create( new AvatarSvg( user.Id, user.Initials ) ),
            requestContext.RequestAborted );
        return requestContext.Render( StatusCodes.Status200OK, Mime.Svg, svg );
    }

    /// <summary>
    /// Whether the template lookup finds <c>show.svg.erb</c> for the request's formats: <c>*/*</c> or SVG, or no
    /// registered format at all.
    /// </summary>
    private static bool SvgTemplateFound( RequestContext requestContext )
    {
        IReadOnlyList<MimeType> formats;
        try
        {
            formats = requestContext.Formats;
        }
        catch ( Exception error ) when ( error is InvalidMimeTypeException or BadRequestException )
        {
            return false;
        }

        return formats.Count == 0 || formats.Any( format => format == Mime.All || format == Mime.Svg );
    }
}
