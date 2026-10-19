using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Application.Integrations.WebPush;
using Campfire.Application.Ruby;
using Campfire.Application.Serialization;
using Campfire.Application.Tests.Integrations.Support;

namespace Campfire.Application.Tests.Integrations.WebPush;

/// <summary>
/// RFC 8291 encryption, VAPID signing and the message encoding against RFC 8291's own example and the web-push gem's
/// output (<c>vectors/integrations/web_push_expected.json</c>, <c>vectors/json/web_push_payload.json</c>).
/// </summary>
public sealed class WebPushEncryptionTests
{
    private static JsonNode Expected => JsonNode.Parse( IntegrationVectors.Text( "web_push_expected.json" ) )!;

    private static byte[] B64( string value ) => RubyBase64.UrlSafeDecode( value )!;

    /// <summary>RFC 8291, section 5.</summary>
    [Fact]
    public void Matches_the_rfc_8291_example( )
    {
        using var server = ECDiffieHellman.Create( new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B64( "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw" ),
        } );
        var plaintext = B64( "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24" );

        var body = WebPushEncryption.Encrypt(
            plaintext, "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4", "BTBZMqHH6r4Tts7J_aSIgg",
            server, B64( "DGv6ra1nlYgDCS1FRnbzlw" ), recordSize: 4096, padding: [2] );

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVB" +
                "St2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            RubyBase64.UrlSafeEncode( body, padding: false ) );
        using var receiver = PushReceiver.FromPrivateKey( "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94", "BTBZMqHH6r4Tts7J_aSIgg" );
        var (recordSize, opened) = receiver.Decrypt( body );
        Assert.Equal( 4096u, recordSize );
        Assert.Equal( [.. plaintext, 2], opened );
    }

    /// <summary>What the gem encrypted opens to its message, framed the gem's way (the record size is the ciphertext's).</summary>
    [Fact]
    public void Decrypts_what_the_gem_encrypted( )
    {
        var expected = Expected;
        using var receiver = PushReceiver.FromPrivateKey( expected["receiver_private_key"]!.GetValue<string>( ), expected["auth"]!.GetValue<string>( ) );
        Assert.Equal( expected["p256dh"]!.GetValue<string>( ).TrimEnd( '=' ), receiver.P256dh );
        var body = B64( expected["ciphertext"]!.GetValue<string>( ) );

        var (recordSize, _) = receiver.Decrypt( body );

        Assert.Equal( ( uint ) body.Length - 86, recordSize );
        Assert.Equal( expected["message"]!.GetValue<string>( ), receiver.Open( body ) );
    }

    [Fact]
    public void Round_trips_with_the_gems_framing( )
    {
        using var receiver = new PushReceiver( );

        var body = WebPushEncryption.Encrypt( "{\"title\":\"hi\"}"u8, receiver.P256dh, receiver.AuthKey );

        Assert.Equal( "{\"title\":\"hi\"}", receiver.Open( body ) );
        Assert.Equal( ( uint ) body.Length - 86, receiver.Decrypt( body ).RecordSize );
    }

    [Fact]
    public void Reads_keys_with_leading_zeros_and_compressed_points_as_openssl_does( )
    {
        using var receiver = new PushReceiver( );
        var point = receiver.Key.ExportParameters( false ).Q;
        var padded = RubyBase64.UrlSafeEncode( [0, 0, 4, .. point.X!, .. point.Y!] );
        var compressed = RubyBase64.UrlSafeEncode( [( byte ) ((point.Y![^1] & 1) == 1 ? 3 : 2), .. point.X!] );

        Assert.Equal( "{}", receiver.Open( WebPushEncryption.Encrypt( "{}"u8, padded, receiver.AuthKey ) ) );
        var fromCompressed = WebPushEncryption.Encrypt( "{}"u8, compressed, receiver.AuthKey );
        Assert.Equal( 16 + 4 + 1 + 65 + 2 + 2 + 16, fromCompressed.Length );
    }

    [Theory]
    [InlineData( "", "YXV0aA", "ArgumentError", false )]
    [InlineData( null, "YXV0aA", "ArgumentError", false )]
    [InlineData( "VALID", "", "ArgumentError", false )]
    [InlineData( "not base64!", "YXV0aA", "ArgumentError", false )]
    [InlineData( "dGVzdF9rZXk", "YXV0aA", "OpenSSL::PKey::EC::Point::Error", false )]
    [InlineData(
        "123-RIXcMgkdjhRnFZaYjjGvo00dydRQbCpQTuXFjLaCPSE7ofxi19awgGc3Doqa1RmYQqsbQDfQTifFZgc",
        "xxx2DtgvmLkevKRwoyahJl0efg",
        "OpenSSL::PKey::EC::Point::Error",
        false )]
    public void Rejects_what_the_gem_rejects( string? p256dh, string auth, string rubyClass, bool invalidates )
    {
        using var receiver = new PushReceiver( );

        var error = Assert.Throws<WebPushDeliveryException>( ( ) => WebPushEncryption.Encrypt( "m"u8, p256dh == "VALID" ? receiver.P256dh : p256dh, auth ) );

        Assert.Equal( (rubyClass, invalidates), (error.RubyClass, error.InvalidatesSubscription) );
    }

    [Fact]
    public void Refuses_a_payload_over_4096_bytes_of_ciphertext( )
    {
        using var receiver = new PushReceiver( );

        // the ciphertext is the message, the two padding bytes and the 16-byte tag
        Assert.Equal( 86 + 4096, WebPushEncryption.Encrypt( new byte[4078], receiver.P256dh, receiver.AuthKey ).Length );
        var error = Assert.Throws<WebPushDeliveryException>( ( ) => WebPushEncryption.Encrypt( new byte[4079], receiver.P256dh, receiver.AuthKey ) );
        Assert.Equal( ("ArgumentError", "encrypted payload is too big"), (error.RubyClass, error.Message) );
    }

    [Fact]
    public void Encodes_the_message_like_json_generate( )
    {
        var message = JsonNode.Parse( Expected["message"]!.GetValue<string>( ) )!;
        var notification = new WebPushNotification(
            message["title"]!.GetValue<string>( ), message["options"]!["body"]!.GetValue<string>( ), "/rooms/1", 3, 1, null, null, null );

        Assert.Equal( NativeJsonEscapes( Expected["message"]!.GetValue<string>( ) ), notification.EncodedMessage( ) );
    }

    /// <summary>The hostile JSON vector keeps Unicode in its source while encoded output escapes line separators and emoji.</summary>
    [Fact]
    public void Encodes_the_hostile_payload_byte_for_byte( )
    {
        var expected = RepositoryPaths.ReadVectorText( "json", "web_push_payload.json" );
        var parsed = JsonPayload.Parse( expected )!;
        var notification = new WebPushNotification(
            parsed["title"]!.GetValue<string>( ), parsed["options"]!["body"]!.GetValue<string>( ), parsed["options"]!["data"]!["path"]!.GetValue<string>( ),
            4, 1, null, null, null );

        var encodedExpected = NativeJsonEscapes( expected );

        Assert.Equal( encodedExpected, notification.EncodedMessage( ) );
        Assert.Contains( "\\u2028", encodedExpected, StringComparison.Ordinal );
        Assert.Contains( "\\u2029", encodedExpected, StringComparison.Ordinal );
        Assert.Contains( "\\uD83D\\uDE00", encodedExpected, StringComparison.Ordinal );
    }

    [Fact]
    public void Signs_the_vapid_header_like_the_gem( )
    {
        var expected = Expected;

        var authorization = FakePushService.Vapid.Authorization( "https://fcm.googleapis.com", expected["now"]!.GetValue<long>( ) );

        Assert.StartsWith( "vapid t=", authorization, StringComparison.Ordinal );
        var (jwt, publicKey) = Split( authorization );
        Assert.Equal( expected["authorization_k"]!.GetValue<string>( ), publicKey );
        var segments = jwt.Split( '.' );
        Assert.Equal( expected["jwt_header_segment"]!.GetValue<string>( ), segments[0] );
        Assert.Equal( expected["jwt_payload_segment"]!.GetValue<string>( ), segments[1] );
        Assert.True( Verifies( publicKey, segments ) );
        Assert.Equal( "mailto:support@37signals.com", expected["vapid_subject"]!.GetValue<string>( ) );
    }

    /// <summary><c>VapidKey.from_keys(nil, nil)</c> signs with a key it just generated, which push services reject.</summary>
    [Fact]
    public void Unset_vapid_keys_sign_with_a_generated_key( )
    {
        var (jwt, publicKey) = Split( new VapidKeys( null, null ).Authorization( "https://fcm.googleapis.com", 1_700_000_000 ) );

        Assert.NotEqual( Expected["authorization_k"]!.GetValue<string>( ), publicKey );
        Assert.True( Verifies( publicKey, jwt.Split( '.' ) ) );
    }

    /// <summary>An unusable server key fails delivery without invalidating a subscription.</summary>
    [Theory]
    [InlineData( "", "qfXLHghuG1rSHZUVo9SscNRI-0EIHRbIrfeGCqbAwak=" )]
    [InlineData( "BEYXTBB5_jNhNzXDmx5KEU55Vbbd-u--Lk9rM5OFQvUkPIBwZJ9QzAq0zdEzFw6yTV8cTriz_qYBVicY02_VxTQ=", "" )]
    [InlineData( "dGVzdF9rZXk", "qfXLHghuG1rSHZUVo9SscNRI-0EIHRbIrfeGCqbAwak=" )]
    public void Unusable_vapid_keys_raise_openssl_errors( string publicKey, string privateKey )
    {
        var error = Assert.Throws<WebPushDeliveryException>( ( ) => new VapidKeys( publicKey, privateKey ).Authorization( "https://fcm.googleapis.com", 0 ) );

        Assert.False( error.InvalidatesSubscription );
        Assert.StartsWith( "OpenSSL::", error.RubyClass, StringComparison.Ordinal );
    }

    private static (string Jwt, string K) Split( string authorization )
    {
        var value = authorization["vapid t=".Length..];
        var comma = value.IndexOf( ",k=", StringComparison.Ordinal );
        return (value[..comma], value[(comma + 3)..]);
    }

    private static bool Verifies( string publicKey, string[] segments )
    {
        var point = B64( publicKey );
        using var key = ECDsa.Create( new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } } );
        return key.VerifyData(
            Encoding.ASCII.GetBytes( $"{segments[0]}.{segments[1]}" ),
            B64( segments[2] ),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation );
    }

    // Keep the imported Ruby/web-push vectors unchanged; encode only their known raw JSON-generator differences.
    private static string NativeJsonEscapes( string json ) => json
        .Replace( "\u2028", "\\u2028", StringComparison.Ordinal )
        .Replace( "\u2029", "\\u2029", StringComparison.Ordinal )
        .Replace( "😀", "\\uD83D\\uDE00", StringComparison.Ordinal )
        .Replace( "\\u001f", "\\u001F", StringComparison.Ordinal );
}
