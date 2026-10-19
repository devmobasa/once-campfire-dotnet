using Campfire.Web.Endpoints.Accounts;
using Campfire.Web.Endpoints.Accounts.Bots;
using Campfire.Web.Endpoints.ActiveStorage;
using Campfire.Web.Endpoints.Autocompletable;
using Campfire.Web.Endpoints.FirstRuns;
using Campfire.Web.Endpoints.Messages;
using Campfire.Web.Endpoints.Messages.Boosts;
using Campfire.Web.Endpoints.Pwa;
using Campfire.Web.Endpoints.QrCode;
using Campfire.Web.Endpoints.Rooms;
using Campfire.Web.Endpoints.Searches;
using Campfire.Web.Endpoints.Sessions;
using Campfire.Web.Endpoints.UnfurlLinks;
using Campfire.Web.Endpoints.Users;
using Campfire.Web.Endpoints.Users.PushSubscriptions;
using Campfire.Web.Endpoints.Welcome;
using Campfire.Web.Hosting;
using Campfire.Web.Http;
using Campfire.Web.Paths;

namespace Campfire.Web.Endpoints;

public static class CampfireEndpoints
{
    public static IEndpointRouteBuilder MapCampfireEndpoints( this IEndpointRouteBuilder endpoints )
    {
        AccountsEndpoints.MapEndpoints( endpoints );
        BotsEndpoints.MapEndpoints( endpoints );
        KeysEndpoints.MapEndpoints( endpoints );
        CustomStylesEndpoints.MapEndpoints( endpoints );
        JoinCodesEndpoints.MapEndpoints( endpoints );
        LogosEndpoints.MapEndpoints( endpoints );
        AccountUsersEndpoints.MapEndpoints( endpoints );
        BlobsProxyEndpoints.MapEndpoints( endpoints );
        BlobsRedirectEndpoints.MapEndpoints( endpoints );
        DiskEndpoints.MapEndpoints( endpoints );
        RepresentationsProxyEndpoints.MapEndpoints( endpoints );
        RepresentationsRedirectEndpoints.MapEndpoints( endpoints );
        AutocompletableUsersEndpoints.MapEndpoints( endpoints );
        FirstRunsEndpoints.MapEndpoints( endpoints );
        MessagesEndpoints.MapEndpoints( endpoints );
        BoostsEndpoints.MapEndpoints( endpoints );
        BotBoostsEndpoints.MapEndpoints( endpoints );
        BotMessagesEndpoints.MapEndpoints( endpoints );
        PwaEndpoints.MapEndpoints( endpoints );
        QrCodeEndpoints.MapEndpoints( endpoints );
        RoomsEndpoints.MapEndpoints( endpoints );
        ClosedsEndpoints.MapEndpoints( endpoints );
        DirectsEndpoints.MapEndpoints( endpoints );
        InvolvementsEndpoints.MapEndpoints( endpoints );
        OpensEndpoints.MapEndpoints( endpoints );
        RefreshesEndpoints.MapEndpoints( endpoints );
        SearchesEndpoints.MapEndpoints( endpoints );
        SessionsEndpoints.MapEndpoints( endpoints );
        TransfersEndpoints.MapEndpoints( endpoints );
        UnfurlLinksEndpoints.MapEndpoints( endpoints );
        UsersEndpoints.MapEndpoints( endpoints );
        AvatarsEndpoints.MapEndpoints( endpoints );
        BansEndpoints.MapEndpoints( endpoints );
        ProfilesEndpoints.MapEndpoints( endpoints );
        PushSubscriptionsEndpoints.MapEndpoints( endpoints );
        TestNotificationsEndpoints.MapEndpoints( endpoints );
        SidebarsEndpoints.MapEndpoints( endpoints );
        WelcomeEndpoints.MapEndpoints( endpoints );

        endpoints.MapMethods( EndpointPaths.Campfire.Up, HttpMethodSets.GetAndHead, ( ) => TypedResults.Content( HealthCheckMiddleware.Body, ContentTypes.HtmlUtf8 ) )
            .AllowAnonymous( );

        endpoints.MapMethods(
                EndpointPaths.Campfire.RecedeHistoricalLocation,
                HttpMethodSets.GetAndHead,
                ( ) => TypedResults.Content( "Going back…", ContentTypes.HtmlUtf8 ) )
            .AllowAnonymous( );

        endpoints.MapMethods(
                EndpointPaths.Campfire.ResumeHistoricalLocation,
                HttpMethodSets.GetAndHead,
                ( ) => TypedResults.Content( "Staying put…", ContentTypes.HtmlUtf8 ) )
            .AllowAnonymous( );

        endpoints.MapMethods(
                EndpointPaths.Campfire.RefreshHistoricalLocation,
                HttpMethodSets.GetAndHead,
                ( ) => TypedResults.Content( "Refreshing…", ContentTypes.HtmlUtf8 ) )
            .AllowAnonymous( );

        return endpoints;
    }
}
