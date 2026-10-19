using Campfire.Application.Data;
using Campfire.Application.Sessions;
using Campfire.Application.Users;
using Campfire.Web.Cable;
using Campfire.Web.Http;
using Campfire.Web.Policy;

namespace Campfire.Web.App;

/// <summary>
/// <see cref="IAuthenticationStore"/> over the database: <c>Authentication::SessionLookup</c>, <c>Session</c> and
/// <c>User.authenticate_bot</c>. <see cref="CurrentUser.Record"/> is the <see cref="User"/> row and
/// <see cref="CurrentSession.Record"/> the <see cref="Session"/> row.
/// </summary>
public sealed class AuthenticationStore( CampfireDatabase database, CableServer cable ) : IAuthenticationStore
{
    /// <summary><c>Session.find_by(token:)</c> and its user (<c>session.user</c>, whatever its status).</summary>
    public async ValueTask<CurrentSession?> FindSessionByTokenAsync( string token, CancellationToken cancellationToken ) =>
        await database.ReadAsync( db =>
        {
            if ( SessionOperations.FindByToken( db, token ) is not { } session
                || UserQueries.FindById( db, session.UserId ) is not { } user )
            {
                return null;
            }

            return ToCurrent( session, user );
        }, cancellationToken );

    /// <summary>
    /// <c>session.resume</c>: at most hourly. The check runs on what the lookup read, so a request only takes the writer
    /// lease when the activity is an hour old.
    /// </summary>
    public async ValueTask<CurrentSession> ResumeSessionAsync(
        CurrentSession session,
        string? userAgent,
        string ipAddress,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( session );

        if ( session.Record is not Session row || !SessionOperations.NeedsActivityRefresh( row, database.Clock.GetUtcNow( ).UtcDateTime ) )
        {
            return session;
        }

        var resumed = await database.WriteAsync( scope => SessionOperations.Resume( scope, session.Id, userAgent, ipAddress ), cancellationToken );
        return resumed is null ? session : session with
        {
            Record = resumed
        };
    }

    public async ValueTask<CurrentUser?> AuthenticateBotAsync( string botKey, CancellationToken cancellationToken )
    {
        var bot = await database.ReadAsync( db => UserQueries.AuthenticateBot( db, botKey ), cancellationToken );
        return bot is null ? null : new CurrentUser( bot.Id, bot );
    }

    /// <summary><c>user.sessions.start!(user_agent:, ip_address:)</c>.</summary>
    public async ValueTask<CurrentSession> StartSessionAsync( CurrentUser user, string? userAgent, string ipAddress, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( user );

        return await database.WriteAsync( scope =>
        {
            var current = UserQueries.FindActiveById( scope.Db, user.Id );
            if ( user.Record is not User verified || current is null || current.IsBot
                || !string.Equals( current.PasswordDigest, verified.PasswordDigest, StringComparison.Ordinal ) )
            {
                throw new HttpFailureException( StatusCodes.Status401Unauthorized, "The verified credential is no longer current." );
            }

            return ToCurrent( SessionOperations.Start( scope, current.Id, userAgent, ipAddress ), current );
        }, cancellationToken );
    }

    public ValueTask DestroySessionAsync( CurrentSession session, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( session );

        return new ValueTask( database.WriteAsync( scope => SessionOperations.Destroy( scope, session.Id ), cancellationToken ) );
    }

    /// <summary><c>Current.user.reset_remote_connections</c>: disconnect with <c>reconnect: true</c>.</summary>
    public ValueTask ResetRemoteConnectionsAsync( CurrentUser user, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( user );

        cable.DisconnectUser( user.Id, reconnect: true );
        return ValueTask.CompletedTask;
    }

    private static CurrentSession ToCurrent( Session session, User user ) =>
        new( session.Id, session.Token, new CurrentUser( user.Id, user ), session );
}
