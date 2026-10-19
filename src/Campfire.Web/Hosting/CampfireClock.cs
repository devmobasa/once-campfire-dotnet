using System.Globalization;

namespace Campfire.Web.Hosting;

/// <summary>
/// The business clock. The parity harness passes libfaketime's <c>FAKETIME</c> (and, when
/// freezing, <c>CAMPFIRE_FROZEN_TIME</c>); the .NET entrypoint reads them itself instead of preloading
/// libfaketime, so Kestrel's <c>Date</c> header stays on the real clock:
/// <list type="bullet">
/// <item><c>CAMPFIRE_FROZEN_TIME</c> (RFC 3339 or <c>YYYY-MM-DD HH:MM:SS</c>): frozen;</item>
/// <item><c>FAKETIME=@YYYY-MM-DD HH:MM:SS</c>: starts at that instant and ticks;</item>
/// <item><c>FAKETIME=YYYY-MM-DD HH:MM:SS</c>: frozen at that instant;</item>
/// <item>otherwise the system clock.</item>
/// </list>
/// Times without a zone are UTC (the images run with <c>TZ=UTC</c>). Only the wall clock is faked;
/// timestamps and timers stay real, as libfaketime leaves the monotonic clock alone.
/// </summary>
public static class CampfireClock
{
    public const string FrozenTimeVariable = "CAMPFIRE_FROZEN_TIME";
    public const string FakeTimeVariable = "FAKETIME";

    /// <summary>The clock the options select.</summary>
    public static TimeProvider Create( CampfireOptions options, TimeProvider system )
    {
        ArgumentNullException.ThrowIfNull( options );
        ArgumentNullException.ThrowIfNull( system );

        if ( options.FrozenTime is { } frozen )
        {
            return new FrozenTimeProvider( ParseInstant( frozen, FrozenTimeVariable ) );
        }

        if ( options.FakeTime is { } fake )
        {
            var trimmed = fake.Trim( );
            return trimmed.StartsWith( '@' )
                ? new OffsetTimeProvider( system, ParseInstant( trimmed[1..], FakeTimeVariable ) - system.GetUtcNow( ) )
                : new FrozenTimeProvider( ParseInstant( trimmed, FakeTimeVariable ) );
        }

        return system;
    }

    /// <summary>Parses <c>YYYY-MM-DD HH:MM:SS</c> (UTC) or an RFC 3339 timestamp; throws naming the variable otherwise.</summary>
    public static DateTimeOffset ParseInstant( string value, string variable )
    {
        ArgumentNullException.ThrowIfNull( value );

        var text = value.Trim( );
        if ( DateTimeOffset.TryParseExact( text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var plain ) )
        {
            return plain.ToUniversalTime( );
        }

        if ( DateTimeOffset.TryParse( text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rfc3339 )
            && text.Contains( 'T', StringComparison.Ordinal ) )
        {
            return rfc3339.ToUniversalTime( );
        }

        throw new InvalidOperationException(
            $"{variable}={value} is not a time the harness passes (\"YYYY-MM-DD HH:MM:SS\", \"@YYYY-MM-DD HH:MM:SS\", or RFC 3339)." );
    }
}
