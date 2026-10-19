using Campfire.Application.Data;
using Campfire.Application.Messages;
using Dapper;

namespace Campfire.Application.Storage;

/// <summary>Durable scalar work for deleted message indexes, detached blobs, and deleted blobs' files.</summary>
public static class CleanupOutbox
{
    private const int PageSize = 64;
    private const string Ready = "next_attempt_at<=@now AND NOT EXISTS (SELECT 1 FROM campfire_cleanup prerequisite WHERE prerequisite.kind=0 AND prerequisite.record_id=campfire_cleanup.blocked_by)";

    internal static void QueueIndex( WriteScope scope, long messageId, long roomId ) =>
        scope.Db.Connection.Execute( "INSERT OR IGNORE INTO campfire_cleanup (kind,record_id,room_id,created_at,next_attempt_at) VALUES (0,@messageId,@roomId,@now,@now)",
            new { messageId, roomId, now = scope.Now }, scope.Db.Transaction );

    internal static void IndexRemoved( CampfireDb db, long messageId ) =>
        db.Connection.Execute( "DELETE FROM campfire_cleanup WHERE kind=0 AND record_id=@messageId", new { messageId }, db.Transaction );

    internal static void QueueBlob( WriteScope scope, long blobId, long? blockedBy = null ) =>
        scope.Db.Connection.Execute( "INSERT INTO campfire_cleanup (kind,record_id,blocked_by,created_at,next_attempt_at) VALUES (1,@blobId,@blockedBy,@now,@now)",
            new { blobId, blockedBy, now = scope.Now }, scope.Db.Transaction );

    internal static void QueueFile( WriteScope scope, Blob blob ) =>
        scope.Db.Connection.Execute( "INSERT OR IGNORE INTO campfire_cleanup (kind,record_id,key,service_name,legacy,created_at,next_attempt_at) VALUES (2,@Id,@Key,@ServiceName,@legacy,@now,@now)",
            new { blob.Id, blob.Key, blob.ServiceName, legacy = blob.IsImage, now = scope.Now }, scope.Db.Transaction );

    internal static void FileRemoved( CampfireDb db, string key, string serviceName ) =>
        db.Connection.Execute( "DELETE FROM campfire_cleanup WHERE kind=2 AND key=@key AND service_name=@serviceName", new { key, serviceName }, db.Transaction );

    internal static async Task<bool> RetryFileAsync( CampfireDatabase database, BlobStorage storage, long blobId, CancellationToken cancellationToken )
    {
        var pending = await database.ReadAsync( db => db.Connection.QueryFirstOrDefault<CleanupWork>(
            "SELECT id,key,legacy FROM campfire_cleanup WHERE kind=2 AND record_id=@blobId ORDER BY id LIMIT 1",
            new { blobId }, db.Transaction ), cancellationToken );
        if ( pending is null )
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested( );
        storage.DeleteFiles( pending.Key ?? throw new InvalidDataException( "File cleanup lacks its storage key." ), pending.Legacy );
        await database.WriteAsync( scope => scope.Db.Connection.Execute( "DELETE FROM campfire_cleanup WHERE id=@Id", pending, scope.Db.Transaction ), cancellationToken );
        return true;
    }

    /// <summary>Processes one bounded page. Failed work stays durable and receives a capped retry delay.</summary>
    public static async Task<int> DrainAsync( CampfireDatabase database, BlobStorage storage, CancellationToken cancellationToken, Action<long, Exception>? failed = null )
    {
        var work = await database.ReadAsync( db => db.Connection.Query<CleanupWork>(
            $"SELECT id,kind,record_id RecordId,key,legacy FROM campfire_cleanup WHERE {Ready} ORDER BY next_attempt_at,id LIMIT @limit",
            new { now = database.Clock.GetUtcNow( ).UtcDateTime, limit = PageSize }, db.Transaction ).AsList( ), cancellationToken );
        var completed = 0;
        foreach ( var item in work )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            try
            {
                switch ( item.Kind )
                {
                    case 0:
                        await database.WriteAsync( scope => MessageSearchIndex.Delete( scope.Db, item.RecordId ), cancellationToken );
                        break;
                    case 1:
                        await BlobOperations.PurgeAsync( database, storage, item.RecordId, cancellationToken );
                        break;
                    case 2:
                        storage.DeleteFiles( item.Key ?? throw new InvalidDataException( "File cleanup lacks its storage key." ), item.Legacy );
                        break;
                    default:
                        throw new InvalidDataException( $"Unknown cleanup kind {item.Kind}." );
                }

                await database.WriteAsync( scope => scope.Db.Connection.Execute( "DELETE FROM campfire_cleanup WHERE id=@Id", item, scope.Db.Transaction ), cancellationToken );
                completed++;
            }
            catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
            {
                throw;
            }
            catch ( Exception error )
            {
                await database.WriteAsync( scope => scope.Db.Connection.Execute(
                    "UPDATE campfire_cleanup SET attempts=MIN(attempts+1,20),next_attempt_at=@next WHERE id=@id",
                    new { id = item.Id, next = scope.Now.AddSeconds( 30 ) }, scope.Db.Transaction ), cancellationToken );
                failed?.Invoke( item.Id, error );
            }
        }

        return completed;
    }

    /// <summary>Counts both durable queues and reports immutable enqueue times separately from due times.</summary>
    public static Task<List<CleanupBacklog>> BacklogAsync( CampfireDatabase database, CancellationToken cancellationToken ) =>
        database.ReadAsync( db => db.Connection.Query<CleanupBacklog>(
            "SELECT 'storage' Queue,COUNT(*) PendingCount,MIN(created_at) OldestCreatedAt,MIN(CASE WHEN next_attempt_at<=@now THEN next_attempt_at END) OldestDueAt FROM campfire_cleanup " +
            "UNION ALL SELECT 'rooms',COUNT(*),MIN(created_at),MIN(CASE WHEN next_attempt_at<=@now THEN next_attempt_at END) FROM campfire_room_deletions",
            new { now = database.Clock.GetUtcNow( ).UtcDateTime }, db.Transaction ).AsList( ), cancellationToken );

    internal sealed class CleanupWork
    {
        public long Id { get; set; }
        public int Kind { get; set; }
        public long RecordId { get; set; }
        public string? Key { get; set; }
        public bool Legacy { get; set; }
    }
}
