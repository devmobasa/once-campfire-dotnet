using System.Text.Json;
using Campfire.Application.Storage;

namespace Campfire.Application.Data.Records;

/// <summary>
/// The commit callbacks of <c>ActiveStorage::Attachment</c>: <c>after_create_commit :mirror_blob_later,
/// :analyze_blob_later, :transform_variants_later</c> and <c>after_destroy_commit :purge_dependent_blob_later</c>.
/// </summary>
/// <remarks>
/// Campfire's disk service does not mirror, and none of its named variants is preprocessed, so on create only the
/// analysis job remains. Every Campfire attachment uses the default <c>dependent: :purge_later</c>.
/// </remarks>
public static class StorageRecords
{
    private const int AnalyzeBlobOrder = 1;

    private const int PurgeBlobOrder = 2;

    /// <summary>
    /// Enlists a new attachment and then its blob (the attachment saves itself and autosaves the blob), and queues
    /// <c>analyze_blob_later</c> unless the blob's metadata says it is analyzed.
    /// </summary>
    public static void RegisterCreated( WriteScope scope, ActiveStorageAttachment attachment, ActiveStorageBlob blob )
    {
        var key = RecordKeys.Attachment( attachment.Id );
        scope.Enlist( key );
        scope.Enlist( RecordKeys.Blob( blob.Id ) );

        if ( !IsAnalyzed( blob.Metadata ) )
        {
            var blobId = blob.Id;
            scope.AfterCommit( key, AnalyzeBlobOrder, "analyze_blob_later", context => context.Hooks.StorageJobs.AnalyzeLater( blobId ) );
        }
    }

    /// <summary>Enlists a destroyed attachment and queues <c>purge_dependent_blob_later</c>.</summary>
    public static void RegisterDestroyed( WriteScope scope, ActiveStorageAttachment attachment )
    {
        var key = RecordKeys.Attachment( attachment.Id );
        var blobId = attachment.BlobId;
        CleanupOutbox.QueueBlob( scope, blobId, attachment.RecordType == "Message" ? attachment.RecordId : null );
        scope.Enlist( key );
        scope.AfterCommit( key, PurgeBlobOrder, "purge_dependent_blob_later", context => context.Hooks.StorageJobs.PurgeLater( blobId ) );
    }

    /// <summary><c>ActiveStorage::Blob#analyzed?</c>: the metadata's <c>analyzed</c> key is truthy.</summary>
    public static bool IsAnalyzed( string? metadata )
    {
        if ( string.IsNullOrWhiteSpace( metadata ) )
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse( metadata );
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty( BlobMetadata.AnalyzedKey, out var analyzed )
                && analyzed.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
        }
        catch ( JsonException )
        {
            return false;
        }
    }
}
