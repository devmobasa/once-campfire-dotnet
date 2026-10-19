using System.Buffers;
using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Campfire.Application.Users;

/// <summary>Identity V3 password creation and imported Rails bcrypt verification, outside database leases.</summary>
public static class Passwords
{
    public const int DefaultIterations = 100_000;
    public const int MinimumIterations = 1_000;
    private const string DummyPassword = "unused-password-for-equal-work";
    private const int MaximumDummyBcryptWorkFactor = 12;
    private static readonly object User = new( );
    private static readonly PasswordHasher<object> Hasher = Create( DefaultIterations );
    private static readonly string DummyDigest = Hasher.HashPassword( User, DummyPassword );
    private static readonly SearchValues<char> BcryptAlphabet = SearchValues.Create( "./ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" );

    public static string Digest( string password, int iterations = DefaultIterations ) => Create( iterations ).HashPassword( User, password );

    public static bool Verify( string password, string? digest )
    {
        if ( string.IsNullOrEmpty( digest ) )
        {
            return false;
        }

        try
        {
            return VerifyResult( password, digest ) != PasswordVerificationResult.Failed;
        }
        catch ( Exception error ) when ( error is FormatException or ArgumentException )
        {
            return false;
        }
    }
    public static void SpendEqualTime( string password, string? bcryptDigest = null ) =>
        Verify( bcryptDigest is null ? password : DummyPassword, bcryptDigest ?? DummyDigest );

    public static PasswordVerificationResult VerifyResult( string password, string digest )
    {
        try
        {
            if ( IsBcryptDigest( digest ) )
            {
                if ( !TryBcryptPassword( password, out var bcryptPassword ) )
                {
                    // Reject Ruby-incompatible input after paying the stored digest's password-work cost.
                    BCrypt.Net.BCrypt.Verify( DummyPassword, digest );
                    return PasswordVerificationResult.Failed;
                }

                return BCrypt.Net.BCrypt.Verify( bcryptPassword, digest )
                    ? PasswordVerificationResult.Success
                    : PasswordVerificationResult.Failed;
            }

            return Hasher.VerifyHashedPassword( User, digest, password );
        }
        catch ( Exception error ) when ( error is FormatException or ArgumentException )
        {
            return PasswordVerificationResult.Failed;
        }
    }

    internal static string CreateDummyBcryptDigest( int workFactor ) =>
        BCrypt.Net.BCrypt.HashPassword( DummyPassword, Math.Clamp( workFactor, 4, MaximumDummyBcryptWorkFactor ) );

    private static bool IsBcryptDigest( string digest ) =>
        digest.Length == 60 && digest.StartsWith( "$2", StringComparison.Ordinal ) && digest[2] is 'a' or 'b' or 'y'
        && digest[3] == '$' && digest[6] == '$'
        && int.TryParse( digest.AsSpan( 4, 2 ), NumberStyles.None, CultureInfo.InvariantCulture, out var cost ) && cost is >= 4 and <= 31
        && digest.AsSpan( 7 ).IndexOfAnyExcept( BcryptAlphabet ) < 0;

    private static bool TryBcryptPassword( string password, out string bcryptPassword )
    {
        var bytes = 0;
        var characters = 0;
        foreach ( var rune in password.EnumerateRunes( ) )
        {
            if ( bytes >= 72 )
            {
                break;
            }

            if ( rune.Value == 0 )
            {
                bcryptPassword = "";
                return false;
            }

            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }

        // Keep the scalar crossing byte 72 intact. BCrypt consumes its raw UTF-8 prefix;
        // decoding a sliced byte sequence would replace that partial scalar and change the hash.
        bcryptPassword = password[..characters];
        return true;
    }

    private static PasswordHasher<object> Create( int iterations )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan( iterations, MinimumIterations );
        return new PasswordHasher<object>( Options.Create( new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterations
        } ) );
    }
}
