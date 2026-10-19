using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Campfire.Web.Security;

/// <summary>Verifies only Rails 8.2 User avatar signed IDs using the configured Rails secret.</summary>
internal sealed class RailsAvatarTokens
{
    internal const string SecretVariable = "SECRET_KEY_BASE";
    private const string AvatarPurpose = "user/avatar";
    private const int MaximumTokenLength = 4096;
    private const int SignatureLength = SHA256.HashSizeInBytes * 2;
    private static readonly SearchValues<char> HexDigits = SearchValues.Create( "0123456789abcdef" );
    private readonly byte[]? _key;

    internal RailsAvatarTokens( string? secret )
    {
        if ( !string.IsNullOrWhiteSpace( secret ) )
        {
            _key = Rfc2898DeriveBytes.Pbkdf2( secret, "active_record/signed_id"u8, 1000, HashAlgorithmName.SHA256, 64 );
        }
    }

    internal long? Verify( string token, DateTimeOffset now, out DateTimeOffset? expiresAt )
    {
        expiresAt = null;
        var separator = token.Length - SignatureLength - 2;
        if ( _key is null || token.Length > MaximumTokenLength || separator <= 0
            || !token.AsSpan( separator, 2 ).SequenceEqual( "--" ) )
        {
            return null;
        }

        Span<byte> supplied = stackalloc byte[SHA256.HashSizeInBytes];
        var signature = token.AsSpan( separator + 2 );
        if ( signature.IndexOfAnyExcept( HexDigits ) >= 0
            || Convert.FromHexString( signature, supplied, out _, out var written ) != OperationStatus.Done || written != supplied.Length )
        {
            return null;
        }

        var encoded = token.AsSpan( 0, separator );
        Span<byte> message = stackalloc byte[MaximumTokenLength];
        for ( var index = 0; index < encoded.Length; index++ )
        {
            var character = encoded[index];
            if ( !char.IsAsciiLetterOrDigit( character ) && character is not '-' and not '_' )
            {
                return null;
            }

            message[index] = ( byte ) character;
        }

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData( _key, message[..encoded.Length], expected );
        if ( !CryptographicOperations.FixedTimeEquals( supplied, expected ) )
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse( Base64Url.DecodeFromChars( encoded ), new JsonDocumentOptions { MaxDepth = 4 } );
            return ReadPayload( payload.RootElement, now, out expiresAt );
        }
        catch ( Exception error ) when ( error is JsonException or FormatException )
        {
            return null;
        }
    }

    private static long? ReadPayload( JsonElement payload, DateTimeOffset now, out DateTimeOffset? expiresAt )
    {
        expiresAt = null;
        if ( payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject( ).Count( ) != 1
            || !payload.TryGetProperty( "_rails", out var metadata )
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty( "pur", out var purpose ) || purpose.ValueKind != JsonValueKind.String
            || purpose.GetString( ) != AvatarPurpose
            || !metadata.TryGetProperty( "data", out var data ) || data.ValueKind != JsonValueKind.Number
            || !data.TryGetInt64( out var id ) )
        {
            return null;
        }

        var fields = 0;
        foreach ( var field in metadata.EnumerateObject( ) )
        {
            if ( field.Name is not "data" and not "pur" and not "exp" )
            {
                return null;
            }

            fields++;
        }

        if ( fields != (metadata.TryGetProperty( "exp", out var expiration ) ? 3 : 2) )
        {
            return null;
        }

        if ( expiration.ValueKind != JsonValueKind.Undefined )
        {
            if ( expiration.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse( expiration.GetString( ), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry )
                || expiry <= now )
            {
                return null;
            }

            expiresAt = expiry;
        }

        return id;
    }
}
