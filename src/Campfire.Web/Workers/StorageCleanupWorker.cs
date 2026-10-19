using Campfire.Application.Rooms;
using Campfire.Application.Storage;
using Campfire.Web.App;
using Campfire.Web.Presenters;

namespace Campfire.Web.Workers;

/// <summary>Resumes durable room and storage cleanup. Shutdown leaves unfinished records for the next process.</summary>
public sealed class StorageCleanupWorker( CampfireApp app, ILogger<StorageCleanupWorker> logger ) : BackgroundService
{
    protected override async Task ExecuteAsync( CancellationToken stoppingToken )
    {
        var rooms = new RoomDeletionCursor( );
        while ( !stoppingToken.IsCancellationRequested )
        {
            var progressed = 0;
            try
            {
                progressed += await CleanupOutbox.DrainAsync( app.Database, app.Storage, stoppingToken,
                    ( id, error ) => logger.LogError( error, "Durable storage cleanup {CleanupId} failed and remains pending", id ) );
            }
            catch ( OperationCanceledException ) when ( stoppingToken.IsCancellationRequested )
            {
                break;
            }
            catch ( Exception error )
            {
                logger.LogError( error, "Durable storage cleanup drain failed and remains pending" );
            }

            try
            {
                progressed += await RoomDeletions.DrainAsync( app.Database,
                    room => app.Broadcasts.RoomRemoved( RoomPresentation.Kind( room.Type ), room.Id ), stoppingToken, rooms,
                    ( id, error ) => logger.LogError( error, "Durable room deletion {RoomId} failed and remains pending", id ) );
            }
            catch ( OperationCanceledException ) when ( stoppingToken.IsCancellationRequested )
            {
                break;
            }
            catch ( Exception error )
            {
                logger.LogError( error, "Durable room deletion drain failed and remains pending" );
            }

            if ( logger.IsEnabled( LogLevel.Debug ) )
            {
                try
                {
                    var now = app.Database.Clock.GetUtcNow( ).UtcDateTime;
                    foreach ( var queue in await CleanupOutbox.BacklogAsync( app.Database, stoppingToken ) )
                    {
                        logger.LogDebug( "Durable cleanup {Queue} pending {PendingCount}, oldest pending age seconds {OldestPendingAgeSeconds}, oldest due age seconds {OldestDueAgeSeconds}",
                            queue.Queue, queue.PendingCount, AgeSeconds( now, queue.OldestCreatedAt ), AgeSeconds( now, queue.OldestDueAt ) );
                    }
                }
                catch ( OperationCanceledException ) when ( stoppingToken.IsCancellationRequested )
                {
                    break;
                }
                catch ( Exception error )
                {
                    logger.LogError( error, "Durable cleanup backlog observation failed" );
                }
            }

            if ( progressed > 0 )
            {
                await Task.Yield( );
            }
            else
            {
                try
                {
                    await Task.Delay( TimeSpan.FromSeconds( 1 ), stoppingToken );
                }
                catch ( OperationCanceledException ) when ( stoppingToken.IsCancellationRequested )
                {
                    break;
                }
            }
        }
    }

    private static double? AgeSeconds( DateTime now, DateTime? at ) => at is { } value ? Math.Max( 0, (now - value).TotalSeconds ) : null;
}
