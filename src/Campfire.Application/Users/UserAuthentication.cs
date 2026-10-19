using System.Globalization;
using System.Runtime.CompilerServices;

using Campfire.Application.Data;

using Dapper;

using Microsoft.AspNetCore.Identity;

namespace Campfire.Application.Users;

/// <summary>
/// Active-user authentication with the password check outside every lease: the row
/// is read on a reader, the reader is returned, then the password is checked. An unknown address still costs
/// password verification, using a bounded synthetic bcrypt digest when the database contains bcrypt users.
/// </summary>
public static class UserAuthentication
{
    private static readonly ConditionalWeakTable<CampfireDatabase, Lazy<Task<string?>>> DummyPolicies = new( );

    /// <summary>
    /// <c>User.active.authenticate_by(email_address:, password:)</c>: the active user with that address whose password
    /// matches, or null. An Identity digest that needs a stronger hash is upgraded in place, unless the password changed meanwhile.
    /// Imported bcrypt digests stay unchanged so Rails can continue using the database.
    /// </summary>
    public static async Task<User?> AuthenticateByAsync(
        CampfireDatabase database, string? emailAddress, string? password, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( database );

        if ( string.IsNullOrEmpty( password ) )
        {
            return null;
        }

        var user = await database.ReadAsync( db => UserQueries.FindActiveByEmailAddress( db, emailAddress ), cancellationToken );
        if ( user is null )
        {
            var policy = DummyPolicies.GetValue( database, static owner =>
                new Lazy<Task<string?>>( ( ) => CreateDummyPolicyAsync( owner ) ) );
            string? bcryptDigest;
            try
            {
                bcryptDigest = await policy.Value.WaitAsync( cancellationToken );
            }
            catch when ( policy.IsValueCreated && policy.Value.IsFaulted )
            {
                DummyPolicies.Remove( database );
                throw;
            }

            Passwords.SpendEqualTime( password, bcryptDigest );
            return null;
        }

        var result = user.PasswordDigest is { } digest ? Passwords.VerifyResult( password, digest ) : PasswordVerificationResult.Failed;
        if ( result == PasswordVerificationResult.Failed )
        {
            return null;
        }

        if ( result == PasswordVerificationResult.SuccessRehashNeeded )
        {
            var upgraded = Passwords.Digest( password );
            // Never overwrite a concurrent password change after the costly hash work.
            user = await database.WriteAsync( scope =>
            {
                var current = UserQueries.FindById( scope.Db, user.Id );
                if ( current?.PasswordDigest == user.PasswordDigest )
                {
                    return UserOperations.Update( scope, user.Id, new UserChanges { PasswordDigest = upgraded } );
                }

                return null;
            }, cancellationToken );
        }

        return user;
    }

    private static async Task<string?> CreateDummyPolicyAsync( CampfireDatabase database )
    {
        // One snapshot per database lifetime. Hash generation occurs after returning the reader lease.
        var digest = await database.ReadAsync( FindBcryptDigest, CancellationToken.None );
        return digest is null ? null : Passwords.CreateDummyBcryptDigest(
            int.Parse( digest.AsSpan( 4, 2 ), NumberStyles.None, CultureInfo.InvariantCulture ) );
    }

    private static string? FindBcryptDigest( CampfireDb db ) => db.Connection.QueryFirstOrDefault<string>(
        """
        SELECT password_digest FROM users
        WHERE length(password_digest) = 60
          AND substr(password_digest, 1, 4) IN ('$2a$', '$2b$', '$2y$')
          AND substr(password_digest, 5, 3) GLOB '[0-3][0-9]$'
          AND substr(password_digest, 5, 2) BETWEEN '04' AND '31'
          AND substr(password_digest, 8) NOT GLOB '*[^./A-Za-z0-9]*'
        ORDER BY substr(password_digest, 5, 2) DESC LIMIT 1
        """, transaction: db.Transaction );
}
