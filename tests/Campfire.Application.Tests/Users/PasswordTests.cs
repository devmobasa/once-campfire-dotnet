using System.Text.Json.Nodes;

using Campfire.Application.Tests.Support;
using Campfire.Application.Users;

using Microsoft.AspNetCore.Identity;

namespace Campfire.Application.Tests.Users;

public sealed class PasswordTests
{
    public static TheoryData<int> RailsChecks => [.. Enumerable.Range( 0, Checks.Count )];

    private static JsonArray Checks => RepositoryPaths.ReadVectorJson( "rails_compat.json" )["passwords"]!["checks"]!.AsArray( );

    private const string RailsDigest = "$2a$12$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66";

    // Synthetic bcrypt-ruby 3.1.22 oracle checks, using fixed salt $2a$04$abcdefghijklmnopqrstuu.
    public static TheoryData<string, string, bool> BcryptBoundaryChecks => new( )
    {
        { new string( 'a', 71 ) + "é", "$2a$04$abcdefghijklmnopqrstuuoVx8RufeJl2sf6nnK3ltb8OHjZZZsny", true },
        { new string( 'a', 71 ) + "ê", "$2a$04$abcdefghijklmnopqrstuuoVx8RufeJl2sf6nnK3ltb8OHjZZZsny", true },
        { new string( 'a', 71 ) + "☃", "$2a$04$abcdefghijklmnopqrstuuoVx8RufeJl2sf6nnK3ltb8OHjZZZsny", false },
        { new string( 'a', 71 ), "$2a$04$abcdefghijklmnopqrstuuoVx8RufeJl2sf6nnK3ltb8OHjZZZsny", false },
        { new string( 'é', 36 ), "$2a$04$abcdefghijklmnopqrstuuKiIlCeXB6chNXkLyAo8C7XcLPzh6zUe", true },
        { new string( 'é', 36 ) + "ignored", "$2a$04$abcdefghijklmnopqrstuuKiIlCeXB6chNXkLyAo8C7XcLPzh6zUe", true },
        { new string( 'é', 35 ) + "ê", "$2a$04$abcdefghijklmnopqrstuuKiIlCeXB6chNXkLyAo8C7XcLPzh6zUe", false },
        { new string( 'é', 35 ), "$2a$04$abcdefghijklmnopqrstuuKiIlCeXB6chNXkLyAo8C7XcLPzh6zUe", false },
        { new string( 'a', 70 ) + "🎉", "$2a$04$abcdefghijklmnopqrstuu4v5r4bbiGW1CPJNFMeNZ4x4r49rIhd6", true },
        { new string( 'a', 70 ) + "😀", "$2a$04$abcdefghijklmnopqrstuu4v5r4bbiGW1CPJNFMeNZ4x4r49rIhd6", true },
        { new string( 'a', 70 ) + "☃", "$2a$04$abcdefghijklmnopqrstuu4v5r4bbiGW1CPJNFMeNZ4x4r49rIhd6", false },
        { new string( 'a', 70 ), "$2a$04$abcdefghijklmnopqrstuu4v5r4bbiGW1CPJNFMeNZ4x4r49rIhd6", false },
        { new string( 'a', 72 ), "$2a$04$abcdefghijklmnopqrstuuBzzIgyKkz7xMWYSzkIjUSnxEQFQ0WNe", true },
        { new string( 'a', 72 ) + "\0ignored", "$2a$04$abcdefghijklmnopqrstuuBzzIgyKkz7xMWYSzkIjUSnxEQFQ0WNe", true },
        { new string( 'a', 71 ) + "\0", "$2a$04$abcdefghijklmnopqrstuuBzzIgyKkz7xMWYSzkIjUSnxEQFQ0WNe", false },
        { "\0" + new string( 'a', 72 ), "$2a$04$abcdefghijklmnopqrstuuBzzIgyKkz7xMWYSzkIjUSnxEQFQ0WNe", false },
        { "a", "$2a$04$abcdefghijklmnopqrstuuMFdJu9yVgmagVAIC24fOZkaFqd3s9JC", true },
        { "a\0a", "$2a$04$abcdefghijklmnopqrstuuMFdJu9yVgmagVAIC24fOZkaFqd3s9JC", false },
        { "\0", "$2a$04$abcdefghijklmnopqrstuuMFdJu9yVgmagVAIC24fOZkaFqd3s9JC", false },
    };

    [Theory]
    [MemberData( nameof( RailsChecks ) )]
    public void Rails_bcrypt_password_checks_match_the_reference( int index )
    {
        var check = Checks[index]!;

        Assert.Equal(
            check["expected"].RequiredBool( ),
            Passwords.Verify( check["password"].RequiredString( ), check["digest"].RequiredString( ) ) );
    }

    [Theory]
    [InlineData( 'a' )]
    [InlineData( 'b' )]
    [InlineData( 'y' )]
    public void Imported_bcrypt_revisions_verify_without_requesting_a_rehash( char revision )
    {
        var digest = "$2" + revision + RailsDigest[3..];

        Assert.Equal( PasswordVerificationResult.Success, Passwords.VerifyResult( "secret123456", digest ) );
        Assert.Equal( PasswordVerificationResult.Failed, Passwords.VerifyResult( "wrong", digest ) );
    }

    [Theory]
    [MemberData( nameof( BcryptBoundaryChecks ) )]
    public void Bcrypt_uses_the_first_72_utf8_bytes_like_ruby( string password, string digest, bool expected )
    {
        Assert.Equal( expected, Passwords.Verify( password, digest ) );
    }

    [Theory]
    [InlineData( "" )]
    [InlineData( "$2a$" )]
    [InlineData( "$2a$03$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66" )]
    [InlineData( "$2a$32$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66" )]
    [InlineData( "$2x$12$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66" )]
    [InlineData( "$2b$xx$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66" )]
    [InlineData( "$2a$12$!/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66" )]
    [InlineData( "$2a$12$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66extra" )]
    public void Malformed_or_unsupported_bcrypt_digests_fail_closed( string digest )
    {
        Assert.Equal( PasswordVerificationResult.Failed, Passwords.VerifyResult( "secret123456", digest ) );
    }

    [Fact]
    public async Task Imported_bcrypt_user_authenticates_without_writing( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var userId = TestDatabase.Id( "david" );
        await testDatabase.ExecuteAsync( "UPDATE users SET password_digest = @p0 WHERE id = @p1", RailsDigest, userId );
        using var observer = testDatabase.OpenSideConnection( );
        using var version = observer.CreateCommand( );
        version.CommandText = "PRAGMA data_version";
        var before = version.ExecuteScalar( );

        var user = await UserAuthentication.AuthenticateByAsync(
            testDatabase.Database, "david@37signals.com", "secret123456", TestDatabase.Cancellation );

        Assert.Equal( userId, user?.Id );
        Assert.Null( await UserAuthentication.AuthenticateByAsync(
            testDatabase.Database, "unknown@example.test", "secret123456", TestDatabase.Cancellation ) );
        Assert.Equal( RailsDigest, await testDatabase.ScalarAsync<string>( "SELECT password_digest FROM users WHERE id = @p0", userId ) );
        Assert.Equal( before, version.ExecuteScalar( ) );
    }

    [Fact]
    public async Task Unknown_email_work_does_not_rescan_users_after_initialization( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( readers: 1 );
        await testDatabase.ExecuteAsync( "UPDATE users SET password_digest = @p0 WHERE id = @p1",
            "$2a$04$abcdefghijklmnopqrstuuMFdJu9yVgmagVAIC24fOZkaFqd3s9JC", TestDatabase.Id( "david" ) );
        StatementLog log;
        using ( var reader = await testDatabase.Database.AcquireReaderAsync( TestDatabase.Cancellation ) )
        {
            log = new StatementLog( reader.Db.Connection );
        }

        using ( log )
        {
            Assert.Null( await UserAuthentication.AuthenticateByAsync(
                testDatabase.Database, "unknown@example.test", "wrong", TestDatabase.Cancellation ) );
            log.Clear( );
            for ( var attempt = 0; attempt < 3; attempt++ )
            {
                Assert.Null( await UserAuthentication.AuthenticateByAsync(
                    testDatabase.Database, "unknown@example.test", "wrong", TestDatabase.Cancellation ) );
            }

            var statements = log.Statements.ToArray( );
            Assert.NotEmpty( statements );
            using var observer = testDatabase.OpenSideConnection( );
            foreach ( var statement in statements )
            {
                using var explain = observer.CreateCommand( );
                explain.CommandText = "EXPLAIN QUERY PLAN " + statement;
                using var rows = explain.ExecuteReader( );
                while ( rows.Read( ) )
                {
                    Assert.DoesNotContain( "SCAN users", rows.GetString( 3 ), StringComparison.OrdinalIgnoreCase );
                }
            }
        }
    }
}
