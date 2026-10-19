using System.Diagnostics;
using Campfire.Application.Data;
using Campfire.Application.Storage;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Storage.Support;

/// <summary>Exercises cleanup recovery through two fresh test processes and an abrupt kill at the acknowledgement boundary.</summary>
internal static class CleanupCrashProcess
{
    private const string RootVariable = "CAMPFIRE_TEST_CLEANUP_CRASH_ROOT";
    private const string PhaseVariable = "CAMPFIRE_TEST_CLEANUP_CRASH_PHASE";
    private const string CrashPhase = "crash";
    private const string RecoveryPhase = "recover";
    private const string ReadyFile = "side-effect.ready";
    private const string CompletedFile = "recovery.complete";
    private const string RetainedFile = "unrelated.txt";
    private const string RetainedContent = "keep this unrelated file";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds( 25 );

    public static bool IsChild => Environment.GetEnvironmentVariable( RootVariable ) is not null;

    public static async Task AssertRecoveryAsync( string method, CancellationToken cancellationToken )
    {
        var root = TestDatabase.NewDirectory( );
        try
        {
            var databasePath = Options( root ).DatabasePath;
            var wal = databasePath + "-wal";
            string key;
            await using ( var crash = Start( root, method, CrashPhase ) )
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
                deadline.CancelAfter( Deadline );
                var ready = Path.Combine( root, ReadyFile );
                while ( !File.Exists( ready ) )
                {
                    Assert.False( crash.Process.HasExited, await crash.FailureIfExitedAsync( ) );
                    await Task.Delay( TimeSpan.FromMilliseconds( 20 ), deadline.Token );
                }

                key = await File.ReadAllTextAsync( ready, deadline.Token );
                Assert.False( File.Exists( FilePath( root, key ) ) );
                Assert.False( crash.Process.HasExited );
                crash.Process.Kill( entireProcessTree: true );
                await crash.Process.WaitForExitAsync( deadline.Token );
                Assert.NotEqual( 0, crash.Process.ExitCode );
                if ( OperatingSystem.IsLinux( ) )
                {
                    Assert.Equal( 137, crash.Process.ExitCode );
                }

                Assert.True( new FileInfo( wal ) is { Exists: true, Length: > 32 }, "An uncleanly closed WAL must remain for the fresh process." );
            }

            await using ( var recovered = Start( root, method, RecoveryPhase ) )
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
                deadline.CancelAfter( Deadline );
                await recovered.Process.WaitForExitAsync( deadline.Token );
                Assert.True( recovered.Process.ExitCode == 0, await recovered.FailureIfExitedAsync( ) );
                Assert.Equal( "complete", await File.ReadAllTextAsync( Path.Combine( root, CompletedFile ), deadline.Token ) );
            }

            Assert.False( File.Exists( FilePath( root, key ) ) );
            Assert.Equal( RetainedContent, await File.ReadAllTextAsync( Path.Combine( root, "files", RetainedFile ), cancellationToken ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    public static async Task RunChildAsync( CancellationToken cancellationToken )
    {
        var root = Environment.GetEnvironmentVariable( RootVariable ) ?? throw new InvalidOperationException( "Missing crash fixture root." );
        var clock = new TestClock( );
        clock.TravelTo( StorageVectors.Now.UtcDateTime );
        var hooks = new RecordingHooks( );
        using var database = CampfireDatabase.Open( Options( root ), clock, hooks.ToHooks( new BasicBodyText( ) ) );
        Directory.CreateDirectory( Path.Combine( root, "tmp" ) );
        using var storage = new BlobStorage( new StorageOptions { Root = Path.Combine( root, "files" ), TempDirectory = Path.Combine( root, "tmp" ) },
            StorageVectors.Verifier, new BlobRecords( database ), clock );
        switch ( Environment.GetEnvironmentVariable( PhaseVariable ) )
        {
            case CrashPhase:
                await CrashAsync( database, storage, root, cancellationToken );
                break;
            case RecoveryPhase:
                await RecoverAsync( database, storage, root, cancellationToken );
                break;
            default:
                throw new InvalidOperationException( "Unknown cleanup crash phase." );
        }
    }

    private static async Task CrashAsync( CampfireDatabase database, BlobStorage storage, string root, CancellationToken cancellationToken )
    {
        using var content = new MemoryStream( "durable cleanup"u8.ToArray( ) );
        var blob = await storage.CreateAndUploadAsync( content, new BlobFilename( "cleanup.txt" ), "text/plain", cancellationToken );
        await File.WriteAllTextAsync( Path.Combine( storage.Disk.Root, RetainedFile ), RetainedContent, cancellationToken );
        await database.WriteAsync( scope =>
        {
            CleanupOutbox.QueueFile( scope, blob );
            Sql.Execute( scope.Db, "DELETE FROM active_storage_blobs WHERE id=@p0", blob.Id );
        }, cancellationToken );
        using var writer = await database.AcquireWriterAsync( cancellationToken );
        writer.Transaction( scope =>
        {
            Sql.Execute( scope.Db, "UPDATE campfire_cleanup SET attempts=9 WHERE kind=2" );
            var pending = CleanupOutbox.DrainAsync( database, storage, cancellationToken );
            Assert.False( pending.IsCompleted );
            Assert.False( storage.Disk.Exists( blob.Key ) );
            var marker = Path.Combine( root, ReadyFile );
            File.WriteAllText( marker + ".tmp", blob.Key );
            File.Move( marker + ".tmp", marker );
            Thread.Sleep( Timeout.Infinite );
        } );
    }

    private static async Task RecoverAsync( CampfireDatabase database, BlobStorage storage, string root, CancellationToken cancellationToken )
    {
        var key = await File.ReadAllTextAsync( Path.Combine( root, ReadyFile ), cancellationToken );
        await database.ReadAsync( db =>
        {
            Assert.Equal( "ok", Sql.Scalar<string>( db, "PRAGMA integrity_check" ) );
            Assert.Equal( "wal", Sql.Scalar<string>( db, "PRAGMA journal_mode" ) );
            Assert.Equal( 1, Sql.Scalar<long>( db, "SELECT COUNT(*) FROM campfire_cleanup WHERE kind=2 AND key=@p0", key ) );
            Assert.Equal( 0, Sql.Scalar<long>( db, "SELECT attempts FROM campfire_cleanup WHERE kind=2 AND key=@p0", key ) );
            Assert.Equal( 0, Sql.Scalar<long>( db, "SELECT COUNT(*) FROM active_storage_blobs" ) );
            return true;
        }, cancellationToken );
        Assert.False( storage.Disk.Exists( key ) );
        Assert.Equal( 1, await CleanupOutbox.DrainAsync( database, storage, cancellationToken ) );
        Assert.Equal( 0, await CleanupOutbox.DrainAsync( database, storage, cancellationToken ) );
        Assert.Equal( 0, await database.ReadAsync( db => Sql.Scalar<long>( db, "SELECT COUNT(*) FROM campfire_cleanup" ), cancellationToken ) );
        await File.WriteAllTextAsync( Path.Combine( root, CompletedFile ), "complete", cancellationToken );
    }

    private static string FilePath( string root, string key ) => Path.Combine( root, "files", key[..2], key[2..4], key );

    private static DatabaseOptions Options( string root ) => new( ) { StorageRoot = root, Environment = "test" };

    private static Child Start( string root, string method, string phase )
    {
        var start = new ProcessStartInfo( Environment.GetEnvironmentVariable( "DOTNET_HOST_PATH" ) ?? "dotnet",
            [typeof( BlobPurgeTests ).Assembly.Location, "-method", method, "-noLogo", "-noColor", "-noAutoReporters", "-parallelMode", "none"] )
        { WorkingDirectory = RepositoryPaths.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment[RootVariable] = root;
        start.Environment[PhaseVariable] = phase;
        return new Child( Process.Start( start ) ?? throw new InvalidOperationException( "Could not start cleanup child." ) );
    }

    private sealed class Child( Process process ) : IAsyncDisposable
    {
        private readonly Task<string> _stdout = process.StandardOutput.ReadToEndAsync( );
        private readonly Task<string> _stderr = process.StandardError.ReadToEndAsync( );
        public Process Process { get; } = process;

        public async Task<string> FailureIfExitedAsync( ) => Process.HasExited ? await _stdout + await _stderr : "Cleanup child is still running.";

        public async ValueTask DisposeAsync( )
        {
            try
            {
                if ( !Process.HasExited )
                {
                    Process.Kill( entireProcessTree: true );
                }

                using var deadline = new CancellationTokenSource( TimeSpan.FromSeconds( 5 ) );
                await Process.WaitForExitAsync( deadline.Token );
                await Task.WhenAll( _stdout, _stderr );
            }
            finally
            {
                Process.Dispose( );
            }
        }
    }
}
