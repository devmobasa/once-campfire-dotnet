using Campfire.Application.Accounts;
using Campfire.Application.Data;
using Campfire.Application.Data.Records;
using Campfire.Application.Messages;
using Campfire.Application.Users;

using Dapper;

namespace Campfire.Application.Storage;

/// <summary>
/// The blob lifecycle steps that span files and rows: <c>blob.analyze</c> with the touches its <c>after_update</c>
/// makes, and <c>blob.purge</c>. The slow file half never holds the writer lease.
/// </summary>
public static class BlobOperations
{
    /// <summary>
    /// <c>blob.analyze</c>: the analyzer's metadata (read outside any lease), then one transaction that saves it and
    /// runs <c>after_update :touch_attachments</c>. Each attachment's <c>touch</c> touches its record: a message (and,
    /// through <c>belongs_to :room, touch: true</c>, its room, with the message's index update after commit), a user
    /// (avatar) or the account (logo). Rails runs the update callbacks even when the metadata comes out unchanged,
    /// as it does when <c>ActiveStorage::AnalyzeJob</c> analyzes a blob <c>process_attachment</c> already analyzed.
    /// </summary>
    public static async Task<Blob> AnalyzeAsync( CampfireDatabase database, BlobStorage storage, Blob blob, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( database );
        ArgumentNullException.ThrowIfNull( storage );
        ArgumentNullException.ThrowIfNull( blob );

        var metadata = await storage.AnalyzedMetadataAsync( blob, cancellationToken );
        var json = metadata.ToJson( );
        await database.WriteAsync( scope =>
        {
            BlobRecords.UpdateMetadata( scope.Db, blob.Id, json );
            TouchAttachments( scope, blob.Id );
        }, cancellationToken );

        return blob with
        {
            Metadata = metadata
        };
    }

    /// <summary><c>Blob#touch_attachments</c>: each attachment's record, oldest attachment first.</summary>
    public static void TouchAttachments( WriteScope scope, long blobId )
    {
        ArgumentNullException.ThrowIfNull( scope );

        var now = scope.Now;
        foreach ( var owner in BlobRecords.Owners( scope.Db, blobId ) )
        {
            switch ( owner.RecordType )
            {
                case Message.RecordType:
                    if ( MessageQueries.FindById( scope.Db, owner.RecordId ) is { } message )
                    {
                        MessageOperations.TouchLater( scope, message );
                    }

                    break;
                case UserOperations.RecordType:
                    UserOperations.Touch( scope.Db, owner.RecordId, now );
                    break;
                case AccountOperations.RecordType:
                    AccountOperations.Touch( scope.Db, owner.RecordId, now );
                    break;
                default:
                    // Variant records and blobs (a preview image's holder) have no timestamps to touch.
                    break;
            }
        }
    }

    /// <summary>
    /// <c>ActiveStorage::Blob#purge</c>: <c>destroy</c>, which Rails refuses while an attachment still points at the
    /// blob (<c>InvalidForeignKey</c>, rescued: nothing happens), destroying the variant records and the preview image
    /// attachment first (their images are purged later, after commit); then the files. Returns whether the blob
    /// was purged.
    /// </summary>
    public static async Task<bool> PurgeAsync( CampfireDatabase database, BlobStorage storage, long blobId, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( database );
        ArgumentNullException.ThrowIfNull( storage );

        var destroyed = await database.WriteAsync( scope => Destroy( scope, blobId ), cancellationToken );
        if ( destroyed is null )
        {
            return await CleanupOutbox.RetryFileAsync( database, storage, blobId, cancellationToken );
        }

        storage.DeleteFiles( destroyed );
        await database.WriteAsync( scope => CleanupOutbox.FileRemoved( scope.Db, destroyed.Key, destroyed.ServiceName ), cancellationToken );
        return true;
    }

    /// <summary>The rows of <c>Blob#destroy</c>, or nothing (and null) while an attachment still points at the blob.</summary>
    private static Blob? Destroy( WriteScope scope, long blobId )
    {
        var db = scope.Db;
        var blob = BlobRecords.FindById( db, blobId );
        var attached = db.Connection.ExecuteScalar<bool>(
            "SELECT EXISTS (SELECT 1 FROM active_storage_attachments WHERE active_storage_attachments.blob_id = @blobId)", new
            {
                blobId
            }, db.Transaction );
        if ( blob is null || attached )
        {
            return null;
        }

        const string children = "(record_type='ActiveStorage::VariantRecord' AND name='image' AND record_id IN (SELECT id FROM active_storage_variant_records WHERE blob_id=@blobId)) OR (record_type='ActiveStorage::Blob' AND record_id=@blobId AND name='preview_image')";
        db.Connection.Execute( $"INSERT INTO campfire_cleanup (kind,record_id,created_at,next_attempt_at) SELECT 1,blob_id,@now,@now FROM active_storage_attachments WHERE {children}",
            new { blobId, now = scope.Now }, db.Transaction );
        db.Connection.Execute( $"DELETE FROM active_storage_attachments WHERE {children}", new { blobId }, db.Transaction );
        db.Connection.Execute( "DELETE FROM active_storage_variant_records WHERE blob_id=@blobId", new { blobId }, db.Transaction );
        CleanupOutbox.QueueFile( scope, blob );
        db.Connection.Execute( "DELETE FROM active_storage_blobs WHERE id = @blobId", new
        {
            blobId
        }, db.Transaction );
        return blob;
    }
}
