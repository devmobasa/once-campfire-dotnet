using Campfire.Application.Users;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Http;
using Campfire.Web.Policy;

namespace Campfire.IntegrationTests.Slice;

public sealed class SessionCredentialTests
{
    [Fact]
    public async Task Password_hash_upgrades_preserve_successful_sign_in( )
    {
        await using var factory = new SliceAppFactory( );
        var digest = Passwords.Digest( "secret123456", Passwords.MinimumIterations );
        using ( var connection = factory.OpenSideConnection( ) )
        using ( var command = connection.CreateCommand( ) )
        {
            command.CommandText = "UPDATE users SET password_digest=$digest WHERE id=$id";
            command.Parameters.AddWithValue( "$digest", digest );
            command.Parameters.AddWithValue( "$id", SliceAppFactory.Id( "david" ) );
            Assert.Equal( 1, command.ExecuteNonQuery( ) );
        }

        using var client = factory.Client( );
        var signedIn = await client.SignInAsync( "david@37signals.com" );

        Assert.Equal( System.Net.HttpStatusCode.Found, signedIn.Status );
        Assert.Contains( Campfire.Web.Security.BrowserAuthentication.CookieName + "=", signedIn.Raw( "Set-Cookie" ) ?? "", StringComparison.Ordinal );
    }

    [Fact]
    public async Task A_password_changed_after_verification_cannot_start_a_session( )
    {
        await using var factory = new SliceAppFactory( );
        var user = await UserAuthentication.AuthenticateByAsync(
            factory.App.Database, "david@37signals.com", "secret123456", TestContext.Current.CancellationToken );
        Assert.NotNull( user );
        var sessions = factory.Scalar<long>( "SELECT COUNT(*) FROM sessions" );
        var replacement = Passwords.Digest( "replacement-password" );
        using ( var connection = factory.OpenSideConnection( ) )
        using ( var command = connection.CreateCommand( ) )
        {
            command.CommandText = "UPDATE users SET password_digest=$digest WHERE id=$id";
            command.Parameters.AddWithValue( "$digest", replacement );
            command.Parameters.AddWithValue( "$id", user.Id );
            Assert.Equal( 1, command.ExecuteNonQuery( ) );
        }

        var store = factory.Services.GetRequiredService<IAuthenticationStore>( );
        var rejected = await Assert.ThrowsAsync<HttpFailureException>( async ( ) =>
            await store.StartSessionAsync( new CurrentUser( user.Id, user ), "credential-race-test", "192.0.2.1", TestContext.Current.CancellationToken ) );

        Assert.Equal( StatusCodes.Status401Unauthorized, rejected.Status );
        Assert.Equal( sessions, factory.Scalar<long>( "SELECT COUNT(*) FROM sessions" ) );
    }
}
