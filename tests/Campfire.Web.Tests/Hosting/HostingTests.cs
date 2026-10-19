using System.Globalization;
using Campfire.Web.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace Campfire.Web.Tests.Hosting;

/// <summary>The environment, the business clock and Kestrel's limits.</summary>
public sealed class HostingTests
{
    [Fact]
    public void Native_key_persistence_does_not_require_a_Rails_secret_key( )
    {
        Assert.Equal( "production", Options( ).Environment );
    }

    [Fact]
    public void Without_settings_the_options_take_the_production_defaults( )
    {
        var options = Options( ("SECRET_KEY_BASE", "abc") );

        Assert.False( options.DisableSsl );
        Assert.Equal( "0", options.AppVersion );
        Assert.Null( options.GitRevision );
        Assert.Equal( 3000, options.Port );
        Assert.Equal( "production", options.Environment );
        Assert.Equal( "info", options.LogLevel );
        Assert.Equal( CampfireOptions.DefaultJobConcurrency( ), options.JobConcurrency );
        Assert.Equal( ( int ) Math.Ceiling( Environment.ProcessorCount * 0.5 ), CampfireOptions.DefaultJobConcurrency( ) );
    }

    [Fact]
    public void The_version_falls_back_to_the_revision( )
    {
        var options = Options( ("SECRET_KEY_BASE", "abc"), ("APP_VERSION", ""), ("GIT_REVISION", "abc123") );

        Assert.Equal( "abc123", options.AppVersion );
        Assert.Equal( "abc123", options.GitRevision );
    }

    [Fact]
    public void Disable_ssl_is_any_non_blank_value( )
    {
        Assert.True( Options( ("SECRET_KEY_BASE", "abc"), ("DISABLE_SSL", "false") ).DisableSsl );
        Assert.False( Options( ("SECRET_KEY_BASE", "abc"), ("DISABLE_SSL", " ") ).DisableSsl );
    }

    [Theory]
    [InlineData( "PORT", "http" )]
    [InlineData( "PORT", "70000" )]
    [InlineData( "PORT", "-1" )]
    [InlineData( "JOB_CONCURRENCY", "0" )]
    public void Invalid_numbers_fail_at_startup( string name, string value ) =>
        Assert.Throws<InvalidOperationException>( ( ) => Options( ("SECRET_KEY_BASE", "abc"), (name, value) ) );

    [Fact]
    public void The_public_origin_is_the_first_tls_domain_as_thruster_reads_it( )
    {
        var unset = Options( ("SECRET_KEY_BASE", "abc") );
        var listed = Options( ("SECRET_KEY_BASE", "abc"), ("TLS_DOMAIN", " Chat.Example.com, ,other.example.com ") );
        var prefixed = Options( ("SECRET_KEY_BASE", "abc"), ("TLS_DOMAIN", "b.example.com"), ("THRUSTER_TLS_DOMAIN", "a.example.com") );

        Assert.Null( unset.PublicHost );
        Assert.Null( unset.PublicOrigin );
        Assert.Equal( "chat.example.com", listed.PublicHost );
        Assert.Equal( "https://chat.example.com", listed.PublicOrigin );
        Assert.Equal( "https://a.example.com", prefixed.PublicOrigin );
    }

    [Theory]
    [InlineData( "http://Chat.Example.com:8080", "http://chat.example.com:8080", "chat.example.com" )]
    [InlineData( "  http://Chat.Example.com:8080  ", "http://chat.example.com:8080", "chat.example.com" )]
    [InlineData( "https://Chat.Example.com:8443/", "https://chat.example.com:8443", "chat.example.com" )]
    [InlineData( "https://chat.example.com:443", "https://chat.example.com", "chat.example.com" )]
    [InlineData( "http://127.0.0.1:8080", "http://127.0.0.1:8080", "127.0.0.1" )]
    [InlineData( "http://[::1]:8080", "http://[::1]:8080", "[::1]" )]
    public void An_explicit_public_origin_preserves_its_scheme_and_public_port( string configured, string expected, string host )
    {
        var options = Options( ("CAMPFIRE_PUBLIC_ORIGIN", configured), ("TLS_DOMAIN", "certificate.example.com") );

        Assert.Equal( expected, options.PublicOrigin );
        Assert.Equal( host, options.PublicHost );
    }

    [Theory]
    [InlineData( "chat.example.com" )]
    [InlineData( "ftp://chat.example.com" )]
    [InlineData( "https://user:password@chat.example.com" )]
    [InlineData( "https://chat.example.com/path" )]
    [InlineData( "https://chat.example.com/../" )]
    [InlineData( "https://chat.example.com?query" )]
    [InlineData( "https://chat.example.com#fragment" )]
    [InlineData( "https://chat.example.com:0" )]
    [InlineData( "https://chat.example.com:65536" )]
    [InlineData( "https://chat.example.com\\" )]
    [InlineData( "https://chat.exam\nple.com" )]
    [InlineData( "http://chat.example.com\r\n" )]
    [InlineData( "\thttp://chat.example.com" )]
    [InlineData( "\n" )]
    public void An_invalid_public_origin_fails_at_startup( string configured ) =>
        Assert.Throws<InvalidOperationException>( ( ) => Options( ("CAMPFIRE_PUBLIC_ORIGIN", configured) ) );

    [Fact]
    public void A_public_origin_does_not_enable_tls_or_hide_an_invalid_tls_domain( )
    {
        var values = new Dictionary<string, string?> { ["CAMPFIRE_PUBLIC_ORIGIN"] = "http://chat.example.com:8080" };
        var configuration = new ConfigurationBuilder( ).AddInMemoryCollection( values ).Build( );

        Assert.Equal( "http://chat.example.com:8080", CampfireOptions.FromConfiguration( configuration ).PublicOrigin );
        Assert.False( FrontServerOptions.FromConfiguration( configuration ).HasTls );
        Assert.Throws<InvalidOperationException>( ( ) => Options( ("CAMPFIRE_PUBLIC_ORIGIN", "http://chat.example.com:8080"), ("TLS_DOMAIN", "invalid/path") ) );
    }

    /// <summary>Thruster takes <c>THRUSTER_TLS_DOMAIN</c> whenever it is set, even empty.</summary>
    [Theory]
    [InlineData( " , ", null )]
    [InlineData( "", null )]
    [InlineData( "b.example.com", "" )]
    public void No_tls_domain_left_means_no_public_origin( string tlsDomain, string? thrusterTlsDomain )
    {
        (string, string)[] variables = [("SECRET_KEY_BASE", "abc"), ("TLS_DOMAIN", tlsDomain)];

        var options = thrusterTlsDomain is null ? Options( variables ) : Options( [.. variables, ("THRUSTER_TLS_DOMAIN", thrusterTlsDomain)] );

        Assert.Null( options.PublicOrigin );
    }

    [Theory]
    [InlineData( "https://chat.example.com" )]
    [InlineData( "chat.example.com:8443" )]
    [InlineData( "chat.example.com/path" )]
    public void A_tls_domain_that_is_not_a_host_fails_at_startup( string value ) =>
        Assert.Throws<InvalidOperationException>( ( ) => Options( ("SECRET_KEY_BASE", "abc"), ("TLS_DOMAIN", value) ) );

    [Fact]
    public void Rails_only_variables_are_accepted_and_ignored( )
    {
        var options = Options(
            ("SECRET_KEY_BASE", "abc"),
            ("RAILS_MAX_THREADS", "5"),
            ("WEB_CONCURRENCY", "2"),
            ("REDIS_URL", "redis://x"),
            ("PIDFILE", "/tmp/p") );

        Assert.Equal( 3000, options.Port );
    }

    /// <summary>The telemetry settings are read (nothing acts on them yet); any nonblank skip value opts out.</summary>
    [Theory]
    [InlineData( "", false )]
    [InlineData( " ", false )]
    [InlineData( "false", true )]
    [InlineData( "1", true )]
    public void Telemetry_configuration_keeps_the_dsn_and_skip_setting( string skip, bool expectedSkip )
    {
        var options = Options( ("SECRET_KEY_BASE", "abc"), ("SENTRY_DSN", "https://test@example.invalid/1"), ("SKIP_TELEMETRY", skip) );

        Assert.Equal( "https://test@example.invalid/1", options.SentryDsn );
        Assert.Equal( expectedSkip, options.SkipTelemetry );
    }

    [Fact]
    public void Without_time_variables_the_clock_is_the_system_clock( )
    {
        var clock = CampfireClock.Create( Options( ("SECRET_KEY_BASE", "abc") ), TimeProvider.System );

        Assert.Same( TimeProvider.System, clock );
    }

    [Fact]
    public void A_frozen_time_freezes_the_wall_clock_only( )
    {
        var clock = CampfireClock.Create( Options( ("SECRET_KEY_BASE", "abc"), ("CAMPFIRE_FROZEN_TIME", "2026-01-01T12:00:00Z") ), TimeProvider.System );

        Assert.Equal( Instant( "2026-01-01T12:00:00Z" ), clock.GetUtcNow( ) );
        Assert.Equal( Instant( "2026-01-01T12:00:00Z" ), clock.GetUtcNow( ) );
        Assert.NotEqual( 0, clock.GetTimestamp( ) );
    }

    [Fact]
    public void Faketime_without_an_at_sign_is_frozen( )
    {
        var clock = CampfireClock.Create( Options( ("SECRET_KEY_BASE", "abc"), ("FAKETIME", "2026-01-01 12:00:00") ), TimeProvider.System );

        Assert.IsType<FrozenTimeProvider>( clock );
        Assert.Equal( Instant( "2026-01-01T12:00:00Z" ), clock.GetUtcNow( ) );
    }

    [Fact]
    public void Faketime_with_an_at_sign_ticks_from_that_instant( )
    {
        var system = new FrozenTimeProvider( Instant( "2030-05-05T00:00:00Z" ) );
        var clock = CampfireClock.Create( Options( ("SECRET_KEY_BASE", "abc"), ("FAKETIME", "@2026-01-01 12:00:00") ), system );

        Assert.Equal( Instant( "2026-01-01T12:00:00Z" ), clock.GetUtcNow( ) );
        system.Advance( TimeSpan.FromSeconds( 90 ) );
        Assert.Equal( Instant( "2026-01-01T12:01:30Z" ), clock.GetUtcNow( ) );
    }

    [Fact]
    public void The_frozen_time_wins_over_faketime_and_bad_specs_fail( )
    {
        var clock = CampfireClock.Create(
            Options( ("SECRET_KEY_BASE", "abc"), ("FAKETIME", "@2020-01-01 00:00:00"), ("CAMPFIRE_FROZEN_TIME", "2026-01-01T12:00:00Z") ),
            TimeProvider.System );

        Assert.Equal( Instant( "2026-01-01T12:00:00Z" ), clock.GetUtcNow( ) );
        Assert.Throws<InvalidOperationException>( ( ) => CampfireClock.Create(
            Options( ("SECRET_KEY_BASE", "abc"), ("FAKETIME", "+2d") ),
            TimeProvider.System ) );
    }

    [Fact]
    public void Kestrel_sends_no_server_header_accepts_any_body_and_listens_on_port( )
    {
        var kestrel = new KestrelServerOptions( );

        CampfireKestrel.Configure( kestrel, Options( ("SECRET_KEY_BASE", "abc"), ("PORT", "3100") ) );

        Assert.False( kestrel.AddServerHeader );
        Assert.Null( kestrel.Limits.MaxRequestBodySize );
        Assert.Equal( 112 * 1024, kestrel.Limits.MaxRequestHeadersTotalSize );
        Assert.Null( kestrel.Limits.MinRequestBodyDataRate );
        Assert.Equal( TimeSpan.FromSeconds( 65 ), kestrel.Limits.KeepAliveTimeout );
    }

    [Fact]
    public void Production_http_options_follow_disable_ssl( )
    {
        var production = CampfireWebSetup.CreateHttpOptions( Options( ("SECRET_KEY_BASE", "abc") ), "/app" );
        var plain = CampfireWebSetup.CreateHttpOptions(
            Options( ("SECRET_KEY_BASE", "abc"), ("DISABLE_SSL", "1"), ("CAMPFIRE_PUBLIC_PATH", "/pub") ),
            "/app" );

        Assert.True( production.ForceSsl );
        Assert.Equal( Path.Combine( "/app", "public" ), production.PublicPath );
        Assert.False( plain.ForceSsl );
        Assert.Equal( "/pub", plain.PublicPath );
    }

    private static DateTimeOffset Instant( string text ) => DateTimeOffset.Parse( text, CultureInfo.InvariantCulture );

    private static CampfireOptions Options( params (string Key, string Value)[] variables )
    {
        var settings = variables.Select( variable => new KeyValuePair<string, string?>( variable.Key, variable.Value ) );
        var configuration = new ConfigurationBuilder( ).AddInMemoryCollection( settings ).Build( );

        return CampfireOptions.FromConfiguration( configuration );
    }
}
