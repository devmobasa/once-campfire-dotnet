using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Campfire.Web.Security;

namespace Campfire.Web.Tests.Security;

public sealed class RailsAvatarTokenTests
{
    private const string Secret = "official-harness-avatar-golden-secret-v1-0123456789abcdef0123456789abcdef";
    // Rails 8.2 User.signed_id_verifier generated these vectors with the public secret above.
    private const string Valid = "eyJfcmFpbHMiOnsiZGF0YSI6MTI3MzI2MTQxLCJwdXIiOiJ1c2VyL2F2YXRhciJ9fQ--a07d8cce97322e6da52f36f12356c2e5abdd67b0429b39659183627b6d1f1353";
    private const string WrongPurpose = "eyJfcmFpbHMiOnsiZGF0YSI6MTI3MzI2MTQxLCJwdXIiOiJ1c2VyL25vdC1hdmF0YXIifX0--ac88bc46f5c9d96f7efc2778599474e1dbaf6f4741662bb7a3f4ec045bd4933c";
    private const string WrongModel = "eyJfcmFpbHMiOnsiZGF0YSI6MTI3MzI2MTQxLCJwdXIiOiJyb29tL2F2YXRhciJ9fQ--f30a72ed85865ff1e34e2ffa4117fd381f52a5e9420bd6a43b876471315211ed";
    private const string Expiring = "eyJfcmFpbHMiOnsiZGF0YSI6MTI3MzI2MTQxLCJleHAiOiIyMDI2LTA5LTI2VDEzOjE1OjAwLjAwMFoiLCJwdXIiOiJ1c2VyL2F2YXRhciJ9fQ--5d11b2241438cf59b4b05a9aa705dd051c9612331a0ffeb89f04092d1c7022c3";
    private static readonly DateTimeOffset Now = new( 2026, 9, 26, 13, 14, 52, TimeSpan.Zero );

    [Theory]
    [InlineData( WrongPurpose )]
    [InlineData( WrongModel )]
    [InlineData( Valid + "x" )]
    [InlineData( "" )]
    [InlineData( "not-a-token" )]
    public void Purpose_model_and_format_are_authenticated( string token )
    {
        var verifier = new RailsAvatarTokens( Secret );
        Assert.Equal( 127326141L, verifier.Verify( Valid, Now, out var expiry ) );
        Assert.Null( expiry );
        Assert.Null( verifier.Verify( token, Now, out _ ) );
    }

    [Fact]
    public void Payload_and_signature_tampering_cannot_verify( )
    {
        var verifier = new RailsAvatarTokens( Secret );
        Assert.Null( verifier.Verify( "f" + Valid[1..], Now, out _ ) );
        Assert.Null( verifier.Verify( "!" + Valid[1..], Now, out _ ) );
        Assert.Null( verifier.Verify( Valid[..^1] + "0", Now, out _ ) );
        Assert.Null( verifier.Verify( Valid[..^1] + "g", Now, out _ ) );
        Assert.Null( verifier.Verify( new string( 'a', 4097 ), Now, out _ ) );
    }

    [Theory]
    [InlineData( "{" )]
    [InlineData( "[]" )]
    [InlineData( """{"_rails":{"data":127326141,"pur":"user/avatar"},"other":true}""" )]
    [InlineData( """{"_rails":{"data":127326141,"data":1,"pur":"user/avatar"}}""" )]
    [InlineData( """{"_rails":{"data":"127326141","pur":"user/avatar"}}""" )]
    [InlineData( """{"_rails":{"data":127326141,"pur":"user/avatar","extra":true}}""" )]
    [InlineData( """{"_rails":{"data":127326141,"pur":"user/avatar","exp":"not-a-time"}}""" )]
    [InlineData( """{"_rails":{"data":127326141,"pur":"user/avatar","exp":null}}""" )]
    public void An_authenticated_payload_still_requires_the_exact_envelope( string json )
    {
        // Authenticate adversarial payloads to reach parsing, rather than failing at the MAC check.
        var key = Rfc2898DeriveBytes.Pbkdf2( Secret, "active_record/signed_id"u8, 1000, HashAlgorithmName.SHA256, 64 );
        var encoded = Base64Url.EncodeToString( Encoding.UTF8.GetBytes( json ) );
        var mac = HMACSHA256.HashData( key, Encoding.ASCII.GetBytes( encoded ) );
        var token = encoded + "--" + Convert.ToHexStringLower( mac );
        Assert.Null( new RailsAvatarTokens( Secret ).Verify( token, Now, out _ ) );
    }

    [Fact]
    public void Expiry_is_checked_against_the_current_clock( )
    {
        var verifier = new RailsAvatarTokens( Secret );
        Assert.Equal( 127326141L, verifier.Verify( Expiring, Now, out var expiry ) );
        Assert.Equal( Now.AddSeconds( 8 ), expiry );
        Assert.Null( verifier.Verify( Expiring, Now.AddSeconds( 8 ), out _ ) );
    }
}
