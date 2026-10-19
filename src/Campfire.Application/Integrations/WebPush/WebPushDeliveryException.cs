using Campfire.Application.Integrations.Network;
using Campfire.Application.Ruby;

namespace Campfire.Application.Integrations.WebPush;

/// <summary>
/// What <c>WebPush.payload_send</c> raised: the gem's <c>ResponseError</c>s for a push service's refusal,
/// <c>ArgumentError</c> for blank or malformed keys and oversized payloads, <c>OpenSSL::OpenSSLError</c> for a key
/// that isn't a P-256 point or a TLS session that fails, and <c>Net::HTTP</c>'s connection errors and timeouts.
/// </summary>
public sealed class WebPushDeliveryException : Exception
{
    public WebPushDeliveryException( ) : this( RubyErrorClasses.WebPushError, "Web Push delivery failed" )
    {
    }

    public WebPushDeliveryException( string message ) : this( RubyErrorClasses.WebPushError, message )
    {
    }

    public WebPushDeliveryException( string message, Exception innerException )
        : this( RubyErrorClasses.WebPushError, message, innerException: innerException )
    {
    }

    public WebPushDeliveryException(
        string rubyClass, string message, bool invalidatesSubscription = false, int? status = null, Exception? innerException = null )
        : base( message, innerException )
    {
        RubyClass = rubyClass;
        InvalidatesSubscription = invalidatesSubscription;
        Status = status;
    }

    /// <summary>The Ruby exception class, as <c>WebPush::Pool</c> logs it.</summary>
    public string RubyClass { get; }

    /// <summary>
    /// Only a push service's 404 or 410 invalidates a subscription. Local key and TLS errors retain it.
    /// </summary>
    public bool InvalidatesSubscription { get; }

    /// <summary>The push service's status, for the gem's <c>ResponseError</c>s.</summary>
    public int? Status { get; }

    internal static WebPushDeliveryException Argument( string message ) => new( RubyErrorClasses.ArgumentError, message );

    internal static WebPushDeliveryException OpenSsl( string rubyClass, string message, Exception? innerException = null ) =>
        new( rubyClass, message, innerException: innerException );

    /// <summary>Connection failures retain subscriptions, including TLS failures.</summary>
    internal static WebPushDeliveryException From( NetHttpException error ) => error.Failure == NetHttpFailure.Tls
        ? OpenSsl( error.RubyClass, error.Message, error )
        : new WebPushDeliveryException( error.RubyClass, error.Message, innerException: error );

    /// <summary>
    /// <c>WebPush::Request#verify_response</c>: null for a 2xx, else the gem's error for the status (and, for a 400,
    /// the reason phrase FCM uses for a bad VAPID key).
    /// </summary>
    internal static WebPushDeliveryException? ForResponse( int status, string reason, string host )
    {
        var rubyClass = status switch
        {
            >= 200 and < 300 => null,
            410 => "WebPush::ExpiredSubscription",
            404 => "WebPush::InvalidSubscription",
            401 or 403 => "WebPush::Unauthorized",
            400 when reason == "UnauthorizedRegistration" => "WebPush::Unauthorized",
            413 => "WebPush::PayloadTooLarge",
            429 => "WebPush::TooManyRequests",
            >= 500 and < 600 => "WebPush::PushServiceError",
            _ => "WebPush::ResponseError",
        };
        return rubyClass is null
            ? null
            : new WebPushDeliveryException( rubyClass, $"host: {host}, status: {status} {reason}", invalidatesSubscription: status is 404 or 410, status: status );
    }
}
