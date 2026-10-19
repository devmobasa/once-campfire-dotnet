using Dapper;

namespace Campfire.Application.Data.Records;

/// <summary>The inserts behind Active Storage's rows, shared by <see cref="AttachedOne"/> and the storage records.</summary>
internal static class StorageRows
{
    /// <summary><c>INSERT INTO active_storage_blobs</c>; returns the new id.</summary>
    /// <exception cref="RecordNotUniqueException">The key is taken.</exception>
    public static long InsertBlob( CampfireDb db, ActiveStorageBlob blob )
    {
        if ( db.Connection.ExecuteScalar<bool>( "SELECT EXISTS(SELECT 1 FROM campfire_cleanup WHERE kind=2 AND key=@Key AND service_name=@ServiceName)", blob, db.Transaction ) )
        {
            throw new InvalidOperationException( "The storage key awaits file cleanup." );
        }

        return DataWrites.Save( ( ) => db.Connection.ExecuteScalar<long>(
            "INSERT INTO active_storage_blobs (byte_size, checksum, content_type, created_at, filename, \"key\", metadata, service_name) " +
            "VALUES (@ByteSize, @Checksum, @ContentType, @CreatedAt, @Filename, @Key, @Metadata, @ServiceName) RETURNING id",
            blob,
            db.Transaction ) );
    }

    /// <summary><c>INSERT INTO active_storage_attachments</c>; returns the new id.</summary>
    /// <exception cref="RecordNotUniqueException">The record already has this blob under this name.</exception>
    public static long InsertAttachment( CampfireDb db, ActiveStorageAttachment attachment ) =>
        DataWrites.Save( ( ) => db.Connection.ExecuteScalar<long>(
            "INSERT INTO active_storage_attachments (blob_id, created_at, name, record_id, record_type) " +
            "VALUES (@BlobId, @CreatedAt, @Name, @RecordId, @RecordType) RETURNING id",
            attachment,
            db.Transaction ) );
}
