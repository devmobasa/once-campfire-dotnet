using System.Collections.Concurrent;
using System.Globalization;
using Campfire.Application.Rooms;
using Campfire.Application.Storage;
using Campfire.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.IntegrationTests.Slice;

public sealed class StorageCleanupWorkerTests
{
    [Fact]
    public async Task More_than_one_page_of_blocked_rooms_does_not_starve_later_deletions_with_a_frozen_clock( )
    {
        using var factory = new SliceAppFactory( );
        var app = factory.App;
        var later = await app.Database.WriteAsync( scope =>
        {
            for ( var index = 0; index < 17; index++ )
            {
                var blocked = RoomOperations.Create( scope, RoomType.Closed, $"blocked-{index}", SliceAppFactory.Id( "david" ) );
                RoomOperations.Destroy( scope, blocked.Id );
                CleanupOutbox.QueueIndex( scope, 100000 + index, blocked.Id );
            }

            using var update = scope.Db.Connection.CreateCommand( );
            update.Transaction = scope.Db.Transaction;
            update.CommandText = "UPDATE campfire_room_deletions SET phase=2; UPDATE campfire_cleanup SET next_attempt_at='2126-01-01 00:00:00' WHERE kind=0";
            update.ExecuteNonQuery( );
            var ready = RoomOperations.Create( scope, RoomType.Closed, "later", SliceAppFactory.Id( "david" ) );
            RoomOperations.Destroy( scope, ready.Id );
            return ready;
        }, TestContext.Current.CancellationToken );

        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={later.Id}" ) == 0 );

        Assert.Equal( 17, factory.Scalar<long>( "SELECT COUNT(*) FROM campfire_room_deletions" ) );
        Assert.Equal( 17, factory.Scalar<long>( "SELECT COUNT(*) FROM campfire_cleanup WHERE kind=0" ) );
    }

    [Fact]
    public async Task A_failed_storage_drain_does_not_skip_room_reset_progress( )
    {
        using var factory = new LoggedFactory( );
        var app = factory.App;
        var room = await app.Database.WriteAsync( scope =>
        {
            var pending = RoomOperations.Create( scope, RoomType.Closed, "independent", SliceAppFactory.Id( "david" ) );
            RoomOperations.Destroy( scope, pending.Id );
            using var rename = scope.Db.Connection.CreateCommand( );
            rename.Transaction = scope.Db.Transaction;
            rename.CommandText = "ALTER TABLE campfire_cleanup RENAME TO campfire_cleanup_unavailable";
            rename.ExecuteNonQuery( );
            return pending;
        }, TestContext.Current.CancellationToken );
        try
        {
            await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT phase FROM campfire_room_deletions WHERE room_id={room.Id}" ) >= 1 );
            Assert.Contains( factory.Logs.Items, item => item.Message == "Durable storage cleanup drain failed and remains pending" && item.Error is not null );
        }
        finally
        {
            factory.Execute( "ALTER TABLE campfire_cleanup_unavailable RENAME TO campfire_cleanup" );
        }

        factory.Clock.Advance( TimeSpan.FromMinutes( 1 ) );
        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={room.Id}" ) == 0 );
    }

    [Fact]
    public async Task Permanently_failing_files_do_not_starve_room_destruction( )
    {
        using var factory = new LoggedFactory( );
        var app = factory.App;
        var room = await app.Database.WriteAsync( scope =>
        {
            var created = RoomOperations.Create( scope, RoomType.Closed, "cleanup progresses", SliceAppFactory.Id( "david" ) );
            for ( var index = 0; index < 31; index++ )
            {
                var key = $"permanentfailure{index}";
                Directory.CreateDirectory( app.Storage.Disk.PathFor( key ) );
                CleanupOutbox.QueueFile( scope, new Blob
                {
                    Id = 100000 + index,
                    Key = key,
                    Filename = new BlobFilename( "x.txt" ),
                    ServiceName = "local",
                } );
            }

            factory.Clock.Advance( TimeSpan.FromMinutes( 2 ) );
            RoomOperations.Destroy( scope, created.Id );
            return created;
        }, TestContext.Current.CancellationToken );

        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={room.Id}" ) == 0 );

        var expectedIds = factory.Scalar<string>( "SELECT group_concat(id) FROM campfire_cleanup WHERE kind=2 ORDER BY id" )!
            .Split( ',' ).Select( value => long.Parse( value, CultureInfo.InvariantCulture ) ).Order( ).ToArray( );
        var failures = factory.Logs.Items.Where( item => item.Fields.ContainsKey( "CleanupId" ) ).ToArray( );
        Assert.Equal( expectedIds, failures.Select( item => ( long ) item.Fields["CleanupId"]! ).Order( ) );
        Assert.All( failures, item => Assert.NotNull( item.Error ) );
        var snapshot = factory.Logs.Items.Last( item => item.Fields.GetValueOrDefault( "Queue" ) as string == "storage" );
        Assert.Equal( 31L, snapshot.Fields["PendingCount"] );
        Assert.Equal( 120d, snapshot.Fields["OldestPendingAgeSeconds"] );
        Assert.Null( snapshot.Fields["OldestDueAgeSeconds"] );
        Assert.Equal( 31, factory.Scalar<long>( "SELECT COUNT(*) FROM campfire_cleanup WHERE kind=2" ) );
        Assert.Equal( 31, factory.Scalar<long>( "SELECT COUNT(*) FROM campfire_cleanup WHERE kind=2 AND attempts=1" ) );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT COUNT(*) FROM campfire_room_deletions WHERE room_id={room.Id}" ) );
    }
    private sealed class LoggedFactory : SliceAppFactory
    {
        public CapturedLogs Logs { get; } = new( );

        protected override void ConfigureWebHost( IWebHostBuilder builder )
        {
            base.ConfigureWebHost( builder );
            builder.ConfigureLogging( logging => logging.AddFilter( "Campfire.Web.Workers.StorageCleanupWorker", LogLevel.Debug ).AddProvider( Logs ) );
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Items { get; } = new( );

        public ILogger CreateLogger( string categoryName ) => categoryName == "Campfire.Web.Workers.StorageCleanupWorker" ? new Capture( this ) : NullLogger.Instance;

        public void Dispose( ) { }

        private sealed class Capture( CapturedLogs owner ) : ILogger
        {
            public IDisposable? BeginScope<TState>( TState state ) where TState : notnull => null;

            public bool IsEnabled( LogLevel logLevel ) => true;

            public void Log<TState>( LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter )
            {
                var fields = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary( ) : new Dictionary<string, object?>( );
                owner.Items.Enqueue( new LogEntry( formatter( state, exception ), exception, fields ) );
            }
        }
    }

    private sealed record LogEntry( string Message, Exception? Error, Dictionary<string, object?> Fields );
}
