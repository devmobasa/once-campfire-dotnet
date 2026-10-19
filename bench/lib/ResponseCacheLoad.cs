#!/usr/bin/env dotnet
#:property PublishAot=false
#:property UseSharedCompilation=false

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

return await CacheRunner.MainAsync( args );

internal sealed class CacheRunner( Dictionary<string, string> options )
{
    private readonly JsonSerializerOptions _json = new( ) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    internal static async Task<int> MainAsync( string[] args )
    {
        if ( args.SequenceEqual( new[] { "--self-test" } ) )
        {
            Contracts.SelfTest( );
            Console.WriteLine( "Response-cache protocol: 20 positive/negative controls passed" );
            return 0;
        }

        if ( args.Length < 1 || args.Length % 2 != 1 )
        {
            return 2;
        }

        var mode = args[0];
        var options = new Dictionary<string, string>( StringComparer.Ordinal );
        for ( var index = 1; index < args.Length; index += 2 )
        {
            options.Add( args[index], args[index + 1] );
        }

        var runner = new CacheRunner( options );
        return mode switch
        {
            "prepare" => runner.Prepare( ),
            "login" => await runner.Login( ),
            "http" => await runner.RunHttp( ),
            "verify" => runner.Verify( ),
            _ => 2,
        };
    }

    internal int Prepare( )
    {
        var database = Get( "--database" );
        if ( Sql( database, "SELECT value FROM ar_internal_metadata WHERE key='campfire_synthetic_fixture'" ).Trim( ) != "1" )
        {
            throw new InvalidOperationException( "Distinct users require an owned disposable synthetic fixture" );
        }

        var room = long.Parse( Get( "--room" ), CultureInfo.InvariantCulture );
        var users = Enumerable.Range( 0, 64 )
            .Select( index => new Viewer(
                2_000_000_001L + index,
                $"cache-viewer-{index:D2}@benchmark.test",
                $"Cache viewer {index:D2}",
                3_000_000_001L + index,
                $"Cache-private-{index:D2}",
                "" ) )
            .ToArray( );
        var sql = new StringBuilder( "BEGIN;" );
        foreach ( var (user, index) in users.Select( ( user, index ) => (user, index) ) )
        {
            // Each viewer takes the --email user's password digest (so --password signs in as any of them); roles alternate.
            var templateEmail = Get( "--email" ).Replace( "'", "''", StringComparison.Ordinal );
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO users(id,name,email_address,password_digest,role,status,created_at,updated_at) "
                + $"SELECT {user.Id},'{user.Name}','{user.Email}',password_digest,{index % 2},0,created_at,updated_at "
                + $"FROM users WHERE email_address='{templateEmail}';" );
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO rooms(id,name,type,creator_id,created_at,updated_at) "
                + $"VALUES({user.PrivateRoom},'{user.PrivateName}','Rooms::Closed',{user.Id},datetime('now'),datetime('now'));" );
            foreach ( var memberRoom in new[] { room, user.PrivateRoom } )
            {
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO memberships(user_id,room_id,involvement,created_at,updated_at) "
                    + $"VALUES({user.Id},{memberRoom},'everything',datetime('now'),datetime('now'));" );
            }

            // One private message per viewer, searchable, which no other viewer may see.
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO messages(id,client_message_id,creator_id,room_id,created_at,updated_at) "
                + $"VALUES({user.PrivateRoom},'cache-secret-{index:D2}',{user.Id},{user.PrivateRoom},datetime('now'),datetime('now'));" );
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO action_text_rich_texts(record_id,record_type,name,body,created_at,updated_at) "
                + $"VALUES({user.PrivateRoom},'Message','body','coffee cache-secret-{index:D2}',datetime('now'),datetime('now'));" );
            sql.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO message_search_index(rowid,body) VALUES({user.PrivateRoom},'coffee cache-secret-{index:D2}');" );
        }

        sql.Append( "COMMIT;" );
        Sql( database, sql.ToString( ) );
        if ( Sql( database, "SELECT count(*) FROM users WHERE email_address LIKE 'cache-viewer-%@benchmark.test'" ).Trim( ) != "64" )
        {
            throw new InvalidOperationException( "Viewer fixture count failed" );
        }

        File.WriteAllText( Get( "--out" ), JsonSerializer.Serialize( users, _json ) );
        return 0;
    }

    internal async Task<int> Login( )
    {
        var users = JsonSerializer.Deserialize<Viewer[]>( File.ReadAllText( Get( "--users" ) ), _json )!;
        using var client = Client( );
        for ( var index = 0; index < users.Length; index++ )
        {
            using var request = Request( HttpMethod.Post, Get( "--base" ) + "/session", "", options.GetValueOrDefault( "--user-agent" ), "identity" );
            // The owned loopback proxy is explicitly trusted in this profile; use one synthetic client per viewer.
            request.Headers.TryAddWithoutValidation( "X-Forwarded-For", "198.51.100." + (index + 1).ToString( CultureInfo.InvariantCulture ) );
            request.Content = new FormUrlEncodedContent( new Dictionary<string, string>
            {
                ["email_address"] = users[index].Email,
                ["password"] = Get( "--password" ),
            } );
            using var response = await client.SendAsync( request );
            if ( response.StatusCode != HttpStatusCode.Found || !response.Headers.TryGetValues( "Set-Cookie", out var cookies ) )
            {
                throw new InvalidOperationException( $"Viewer {index} login failed: HTTP {( int ) response.StatusCode}" );
            }

            var cookie = cookies.Single( field => field.StartsWith( "session_token=", StringComparison.Ordinal ) ).Split( ';' )[0];
            users[index] = users[index] with { Cookie = cookie };
        }

        File.WriteAllText( Get( "--out" ), JsonSerializer.Serialize( users, _json ) );
        return 0;
    }

    internal async Task<int> RunHttp( )
    {
        var viewers = options.TryGetValue( "--users", out var usersPath ) ? JsonSerializer.Deserialize<Viewer[]>( File.ReadAllText( usersPath ), _json )! : [];
        var concurrency = int.Parse( Get( "--conc" ), CultureInfo.InvariantCulture );
        if ( viewers.Length != 0 && (viewers.Length < 64 || concurrency > viewers.Length) )
        {
            throw new InvalidOperationException( "Each connection requires one distinct viewer from a cohort of at least 64" );
        }

        var duration = double.Parse( Get( "--duration" ), CultureInfo.InvariantCulture );
        var gzipAccepted = options.GetValueOrDefault( "--gzip", "1" ) == "1";
        var path = Get( "--path" );
        var origin = Get( "--base" ).TrimEnd( '/' );
        var agent = options.GetValueOrDefault( "--user-agent" );
        var expected = options.GetValueOrDefault( "--expect-body", "" ).Split( '|' );
        var cpuStart = Cpu( Get( "--server-cgroup" ) );
        var generatorCpu = Process.GetCurrentProcess( ).TotalProcessorTime;
        var watch = Stopwatch.StartNew( );
        var startedUtc = DateTimeOffset.UtcNow;
        var ack = new Acknowledgement( "" );
        Task<Writer> writer;
        if ( options.TryGetValue( "--post-room", out var writeRoom ) )
        {
            writer = WriteAsync( origin, Get( "--cookie" ), writeRoom, agent, ack, watch, duration );
        }
        else
        {
            writer = Task.FromResult( new Writer( 0, 0, "", 0, 0 ) );
        }

        using var admission = new SemaphoreSlim( concurrency );
        var workerCount = viewers.Length == 0 ? concurrency : viewers.Length;
        var workers = Enumerable.Range( 0, workerCount )
            .Select( index => RunWorker( index, viewers, Get( "--cookie" ), origin, path, agent, gzipAccepted, expected, watch, duration, ack, admission ) )
            .ToArray( );
        var samples = await Task.WhenAll( workers );
        var written = await writer;
        var elapsed = watch.Elapsed.TotalSeconds;
        written = written with { AchievedPerSecond = written.Successful / elapsed };
        var cpu = Cpu( Get( "--server-cgroup" ) ) - cpuStart;
        var latency = samples.SelectMany( sample => sample.Latencies ).Order( ).ToArray( );
        var responses = latency.Length;
        var generatorSeconds = (Process.GetCurrentProcess( ).TotalProcessorTime - generatorCpu).TotalSeconds;
        var result = new
        {
            conc = concurrency,
            gzip = gzipAccepted,
            path,
            secs = elapsed,
            rps = responses / elapsed,
            ok = responses,
            errors = samples.Sum( sample => sample.Errors ),
            mismatches = samples.Sum( sample => sample.Mismatches ),
            avg_bytes = responses == 0 ? 0 : samples.Sum( sample => sample.Bytes ) / responses,
            started_unix_us = startedUtc.ToUnixTimeMilliseconds( ) * 1000,
            ended_unix_us = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds( ) * 1000,
            latency = new
            {
                n = responses,
                mean_ms = latency.Length == 0 ? 0 : latency.Average( ),
                p50_ms = Percentile( latency, 0.5 ),
                p90_ms = Percentile( latency, 0.9 ),
                p95_ms = Percentile( latency, 0.95 ),
                p99_ms = Percentile( latency, 0.99 ),
                max_ms = latency.LastOrDefault( ),
            },
            server_cpu = new
            {
                cpu_secs = cpu,
                cpu_ms_per_response = responses == 0 ? 0 : cpu * 1000 / responses,
                scope = "whole container, cgroup user plus system CPU, completed HTTP responses",
            },
            loadgen = new
            {
                cpu_secs = generatorSeconds,
                wall_secs = elapsed,
                cpus = Environment.ProcessorCount,
                util = generatorSeconds / elapsed / Environment.ProcessorCount,
                debug_logging = false,
            },
            statuses = Merge( samples.Select( sample => sample.Statuses.Select( StatusCount ) ) ),
            validation = new
            {
                bodies_checked = responses,
                @checked = responses,
                mismatches = samples.Sum( sample => sample.Mismatches ),
                stale_reads = samples.Sum( sample => sample.Stale ),
                distinct_viewers = viewers.Length == 0 ? 1 : viewers.Length,
                content_encodings = Merge( samples.Select( sample => sample.Encodings.AsEnumerable( ) ) ),
                error_samples = Array.Empty<string>( ),
                samples = Array.Empty<string>( ),
                kinds = new Dictionary<string, int>( ),
                per_connection_bodies_checked = samples.Select( sample => sample.Latencies.Count ).ToArray( ),
                viewer_cohort = viewers.Length,
            },
            paced_writer = written,
            user_agent = agent,
        };
        var serialized = JsonSerializer.Serialize( result, _json );
        if ( options.TryGetValue( "--out", out var output ) )
        {
            Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( output ) )! );
            File.WriteAllText( output, serialized );
        }

        Console.WriteLine( serialized );
        return result.errors != 0 || result.mismatches != 0 || written.Errors != 0 ? 1 : 0;
    }

    internal int Verify( )
    {
        using var document = JsonDocument.Parse( File.ReadAllText( Get( "--result" ) ) );
        var root = document.RootElement;
        if ( root.GetProperty( "errors" ).GetInt32( ) != 0
            || root.GetProperty( "mismatches" ).GetInt32( ) != 0
            || root.GetProperty( "validation" ).GetProperty( "stale_reads" ).GetInt32( ) != 0 )
        {
            throw new InvalidOperationException( "Read validation failed" );
        }

        var writer = root.GetProperty( "paced_writer" );
        var count = writer.GetProperty( "successful" ).GetInt32( );
        if ( writer.GetProperty( "errors" ).GetInt32( ) != 0 )
        {
            throw new InvalidOperationException( "Writer failed" );
        }

        if ( count > 0 )
        {
            var room = writer.GetProperty( "room" ).GetInt64( );
            var prefix = writer.GetProperty( "body_prefix" ).GetString( )!.Replace( "'", "''", StringComparison.Ordinal );
            var database = Get( "--database" );
            var persistedQuery = "SELECT count(*) FROM messages m JOIN action_text_rich_texts r "
                + $"ON r.record_type='Message' AND r.record_id=m.id WHERE m.room_id={room} AND instr(r.body,'{prefix}')>0";
            var persisted = int.Parse( Sql( database, persistedQuery ), CultureInfo.InvariantCulture );
            var rate = writer.GetProperty( "achieved_per_second" ).GetDouble( );
            if ( persisted != count || rate < 0.9 || rate > 1.1 )
            {
                throw new InvalidOperationException( "Independent same-room commit count or 1/s rate failed" );
            }

            Console.WriteLine( $"Validated {count} independently persisted same-room posts; {rate:F3}/s; zero stale reads" );
        }

        var validation = root.GetProperty( "validation" );
        if ( validation.GetProperty( "viewer_cohort" ).GetInt32( ) > 0 && !EveryViewerValidated( validation ) )
        {
            throw new InvalidOperationException( "Every distinct viewer must receive a validated body" );
        }

        return 0;
    }

    /// <summary>At least 64 distinct viewers, and every connection checked at least one body.</summary>
    private static bool EveryViewerValidated( JsonElement validation ) =>
        validation.GetProperty( "distinct_viewers" ).GetInt32( ) >= 64
        && !validation.GetProperty( "per_connection_bodies_checked" ).EnumerateArray( ).Any( number => number.GetInt32( ) == 0 );

    private string Get( string name ) => options[name];

    private static async Task<Sample> RunWorker(
        int index,
        Viewer[] viewers,
        string fallbackCookie,
        string origin,
        string path,
        string? agent,
        bool gzipAccepted,
        string[] expected,
        Stopwatch watch,
        double duration,
        Acknowledgement ack,
        SemaphoreSlim admission )
    {
        using var client = Client( );
        var viewer = viewers.Length == 0 ? null : viewers[index];
        var cookie = viewer?.Cookie ?? fallbackCookie;
        var samples = new List<double>( );
        long bytes = 0;
        var errors = 0;
        var mismatches = 0;
        var stale = 0;
        var counts = new Dictionary<int, int>( );
        var encodings = new Dictionary<string, int>( StringComparer.Ordinal );
        while ( watch.Elapsed.TotalSeconds < duration )
        {
            await admission.WaitAsync( );
            var required = Volatile.Read( ref ack.Marker ); // Snapshot before starting this read, never after it.
            var start = Stopwatch.GetTimestamp( );
            try
            {
                using var request = Request( HttpMethod.Get, origin + path, cookie, agent, gzipAccepted ? "gzip" : "identity" );
                using var response = await client.SendAsync( request );
                var wire = await response.Content.ReadAsByteArrayAsync( );
                var encoding = response.Content.Headers.ContentEncoding.SingleOrDefault( ) ?? "identity";
                encodings[encoding] = encodings.GetValueOrDefault( encoding ) + 1;
                var plain = Decode( wire, encoding );
                bytes += wire.Length;
                counts[( int ) response.StatusCode] = counts.GetValueOrDefault( ( int ) response.StatusCode ) + 1;
                var fresh = Contracts.Fresh( path, plain, required );
                if ( !fresh )
                {
                    stale++;
                }

                if ( response.StatusCode != HttpStatusCode.OK
                    || response.Content.Headers.ContentType?.MediaType != "text/html"
                    || !expected.Any( field => plain.Contains( field, StringComparison.Ordinal ) )
                    || !Contracts.ViewerBody( path, plain, viewer, viewers )
                    || !fresh )
                {
                    mismatches++;
                }

                samples.Add( Stopwatch.GetElapsedTime( start ).TotalMilliseconds );
            }
            catch ( Exception error ) when ( error is HttpRequestException or TaskCanceledException or InvalidDataException or IOException )
            {
                errors++;
            }
            finally
            {
                admission.Release( );
            }
        }

        return new Sample( samples, bytes, errors, mismatches, stale, counts, encodings );
    }

    /// <summary>The latency at <paramref name="fraction"/> of the sorted samples (nearest rank), or 0 with none.</summary>
    private static double Percentile( double[] sorted, double fraction )
    {
        if ( sorted.Length == 0 )
        {
            return 0;
        }

        return sorted[Math.Min( sorted.Length - 1, ( int ) Math.Ceiling( sorted.Length * fraction ) - 1 )];
    }

    private static KeyValuePair<string, int> StatusCount( KeyValuePair<int, int> status ) =>
        new( status.Key.ToString( CultureInfo.InvariantCulture ), status.Value );

    private static HttpClient Client( )
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 1,
        };

        return new HttpClient( handler ) { Timeout = TimeSpan.FromSeconds( 10 ) };
    }

    private static HttpRequestMessage Request( HttpMethod method, string url, string cookie, string? agent, string encoding )
    {
        var request = new HttpRequestMessage( method, url );
        request.Headers.TryAddWithoutValidation( "Cookie", cookie );
        request.Headers.TryAddWithoutValidation( "Accept", "text/html" );
        request.Headers.TryAddWithoutValidation( "Accept-Encoding", encoding );
        request.Headers.TryAddWithoutValidation( "Sec-Fetch-Site", "same-origin" );
        request.Headers.TryAddWithoutValidation( "Origin", new Uri( url ).GetLeftPart( UriPartial.Authority ) );
        if ( agent is not null )
        {
            request.Headers.TryAddWithoutValidation( "User-Agent", agent );
        }

        return request;
    }

    private static string Decode( byte[] wire, string encoding )
    {
        if ( encoding == "identity" )
        {
            return Encoding.UTF8.GetString( wire );
        }

        if ( encoding != "gzip" )
        {
            throw new InvalidDataException( "Unexpected content encoding" );
        }

        using var source = new MemoryStream( wire );
        using var gzip = new GZipStream( source, CompressionMode.Decompress );
        using var reader = new StreamReader( gzip, Encoding.UTF8 );
        return reader.ReadToEnd( );
    }

    /// <summary>The cgroup's CPU time so far (user plus system), in seconds, from its cpu.stat.</summary>
    private static double Cpu( string cgroup )
    {
        var usage = File.ReadLines( Path.Combine( cgroup, "cpu.stat" ) ).Single( line => line.StartsWith( "usage_usec ", StringComparison.Ordinal ) );

        return double.Parse( usage.Split( ' ' )[1], CultureInfo.InvariantCulture ) / 1_000_000;
    }

    /// <summary>Sums each key's counts across the workers.</summary>
    private static Dictionary<string, int> Merge( IEnumerable<IEnumerable<KeyValuePair<string, int>>> groups ) =>
        groups.SelectMany( group => group )
            .GroupBy( pair => pair.Key )
            .ToDictionary( group => group.Key, group => group.Sum( pair => pair.Value ) );

    private static string Sql( string database, string sql )
    {
        var startInfo = new ProcessStartInfo( "sqlite3" ) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        startInfo.ArgumentList.Add( "-bail" );
        startInfo.ArgumentList.Add( database );
        using var process = Process.Start( startInfo )!;
        process.StandardInput.WriteLine( sql );
        process.StandardInput.Close( );
        var output = process.StandardOutput.ReadToEnd( );
        var error = process.StandardError.ReadToEnd( );
        process.WaitForExit( );
        if ( process.ExitCode != 0 )
        {
            throw new InvalidOperationException( error );
        }

        return output;
    }

    /// <summary>Posts one marked message a second into <paramref name="room"/> while the reads run, acknowledging each one.</summary>
    private static async Task<Writer> WriteAsync(
        string origin,
        string cookie,
        string room,
        string? agent,
        Acknowledgement ack,
        Stopwatch watch,
        double duration )
    {
        using var client = Client( );
        var prefix = "cache-fresh-" + Guid.NewGuid( ).ToString( "N" ) + "-";
        var count = 0;
        var errors = 0;
        var next = 0.0;
        while ( Contracts.WriterWindow( next, watch.Elapsed.TotalSeconds, duration ) )
        {
            var remaining = next - watch.Elapsed.TotalSeconds;
            if ( remaining > 0 )
            {
                await Task.Delay( TimeSpan.FromSeconds( Math.Min( remaining, 0.05 ) ) );
                continue;
            }

            var marker = prefix + count.ToString( CultureInfo.InvariantCulture );
            using var request = Request( HttpMethod.Post, origin + "/rooms/" + room + "/messages", cookie, agent, "identity" );
            request.Headers.Remove( "Accept" );
            request.Headers.TryAddWithoutValidation( "Accept", "text/vnd.turbo-stream.html" );
            request.Content = new FormUrlEncodedContent( new Dictionary<string, string>
            {
                ["message[body]"] = marker,
                ["message[client_message_id]"] = marker,
            } );
            using var response = await client.SendAsync( request );
            var body = await response.Content.ReadAsStringAsync( );
            if ( response.StatusCode == HttpStatusCode.OK && body.Contains( marker, StringComparison.Ordinal ) )
            {
                count++;
                Volatile.Write( ref ack.Marker, marker );
            }
            else
            {
                errors++;
            }

            next = Math.Max( next + 1, watch.Elapsed.TotalSeconds );
        }

        // The caller computes the rate over the complete read window, including its final idle interval.
        return new Writer( count, errors, prefix, 0, long.Parse( room, CultureInfo.InvariantCulture ) );
    }
}

internal sealed record Viewer( long Id, string Email, string Name, long PrivateRoom, string PrivateName, string Cookie );

internal sealed record Writer( int Successful, int Errors, string BodyPrefix, double AchievedPerSecond, long Room );

/// <summary>The newest post the writer has seen committed; a room read that starts after it must show it.</summary>
internal sealed class Acknowledgement( string marker )
{
    internal string Marker = marker;
}

internal sealed record Sample(
    List<double> Latencies,
    long Bytes,
    int Errors,
    int Mismatches,
    int Stale,
    Dictionary<int, int> Statuses,
    Dictionary<string, int> Encodings );

internal static class Contracts
{
    private const string ViewerEmailPrefix = "cache-viewer-";

    internal static bool WriterWindow( double scheduled, double now, double duration ) => scheduled < duration && now < duration;

    /// <summary>Whether a room page shows the last acknowledged post (other pages, and reads before any post, pass).</summary>
    internal static bool Fresh( string path, string plain, string acknowledged ) =>
        acknowledged.Length == 0
        || !path.StartsWith( "/rooms/", StringComparison.Ordinal )
        || plain.Contains( acknowledged, StringComparison.Ordinal );

    internal static bool ViewerBody( string path, string plain, Viewer? viewer, Viewer[] all )
    {
        if ( viewer is null )
        {
            return true;
        }

        if ( path.Contains( "/messages", StringComparison.Ordinal ) )
        {
            // The shared room's fragments are viewer-independent, but no private-room body may leak into them.
            return !all.Any( user => plain.Contains( user.PrivateName, StringComparison.Ordinal )
                || plain.Contains( SecretOf( user ), StringComparison.Ordinal ) );
        }

        // Room layouts load the sidebar lazily; the viewer ID is present, private room names are not.
        if ( !path.StartsWith( "/rooms/", StringComparison.Ordinal ) && !plain.Contains( viewer.PrivateName, StringComparison.Ordinal ) )
        {
            return false;
        }

        if ( all.Any( other => other.Id != viewer.Id && plain.Contains( other.PrivateName, StringComparison.Ordinal ) ) )
        {
            return false;
        }

        if ( path.StartsWith( "/searches", StringComparison.Ordinal ) )
        {
            if ( !plain.Contains( SecretOf( viewer ), StringComparison.Ordinal ) )
            {
                return false;
            }

            if ( all.Any( other => other.Id != viewer.Id && plain.Contains( SecretOf( other ), StringComparison.Ordinal ) ) )
            {
                return false;
            }
        }

        return plain.Contains( $"name=\"current-user-id\" content=\"{viewer.Id}\"", StringComparison.Ordinal );
    }

    internal static void SelfTest( )
    {
        if ( !WriterWindow( 1, 1, 2 ) || WriterWindow( 2, 1.999, 2 ) || WriterWindow( 1, 2, 2 ) )
        {
            throw new InvalidOperationException( "Writer window must exclude the final scheduled boundary" );
        }

        Viewer[] viewers =
        [
            new Viewer( 1, "cache-viewer-00@benchmark.test", "one", 11, "Cache-private-00", "" ),
            new Viewer( 2, "cache-viewer-01@benchmark.test", "two", 12, "Cache-private-01", "" ),
        ];
        var plain = "Cache-private-00 name=\"current-user-id\" content=\"1\" coffee cache-secret-00";
        foreach ( var path in new[] { "/rooms/1", "/searches?q=coffee", "/users/me/sidebar" } )
        {
            if ( !ViewerBody( path, plain, viewers[0], viewers )
                || ViewerBody( path, plain, viewers[1], viewers )
                || ViewerBody( path, plain + " Cache-private-01", viewers[0], viewers ) )
            {
                throw new InvalidOperationException( "Viewer isolation protocol failed" );
            }
        }

        if ( ViewerBody( "/searches?q=coffee", plain + " cache-secret-01", viewers[0], viewers )
            || Fresh( "/rooms/1", "older", "new post" )
            || !Fresh( "/rooms/1/messages", "new post", "new post" )
            || !Fresh( "/searches", "unchanged", "new post" ) )
        {
            throw new InvalidOperationException( "Freshness/isolation negative control failed" );
        }

        var room = "name=\"current-user-id\" content=\"1\"";
        if ( !ViewerBody( "/rooms/1", room, viewers[0], viewers )
            || ViewerBody( "/rooms/1", room, viewers[1], viewers )
            || !ViewerBody( "/rooms/1/messages", "shared message", viewers[0], viewers )
            || ViewerBody( "/rooms/1/messages", plain, viewers[0], viewers ) )
        {
            throw new InvalidOperationException( "Lazy-sidebar and shared-message positive/negative controls failed" );
        }
    }

    /// <summary>The marker of the viewer's private message: cache-secret- and the viewer's two-digit number.</summary>
    private static string SecretOf( Viewer viewer ) =>
        "cache-secret-" + viewer.Email.AsSpan( ViewerEmailPrefix.Length, 2 ).ToString( );
}
