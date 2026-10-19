using Campfire.Application.Data;
using Campfire.Application.Data.Records;
using Campfire.Application.Storage;

namespace Campfire.Application.Messages;

/// <summary>
/// <c>message.update!(attachment: value)</c> for the values <see cref="MessageOperations.Update"/> doesn't take: a
/// signed blob id (<c>CreateOne</c> with a <c>String</c>) and <c>nil</c> or <c>""</c> (<c>DeleteOne</c>). Call it in
/// the same write scope, after <see cref="MessageOperations.Update"/>: Rails saves the message's own columns and its
/// body before the attachment change, so the message's <c>update_in_index</c> stays first after commit.
/// </summary>
public static class MessageAttachments
{
    /// <summary>
    /// Attaches the blob in place of the current attachment (purged after commit); the new attachment's
    /// <c>belongs_to :record, touch: true</c> touches the message and so its room. The blob the message has already
    /// keeps its attachment and touches nothing. Every assignment refreshes the search text after commit.
    /// Returns the blob and the attachment.
    /// </summary>
    /// <exception cref="RecordNotFoundException">The message or the blob is gone.</exception>
    public static AttachedBlob AttachBlob( WriteScope scope, long messageId, SignedBlobAttachment blob )
    {
        ArgumentNullException.ThrowIfNull( scope );
        ArgumentNullException.ThrowIfNull( blob );

        var message = MessageQueries.Find( scope.Db, messageId );
        scope.Enlist( RecordKeys.Message( messageId ) );
        var attached = AttachedOne.AttachBlob( scope, new AttachmentOwner( Message.RecordType, message.Id ), Message.AttachmentName, blob );
        MessageOperations.ReindexAfterCommit( scope, messageId );
        if ( attached.Created )
        {
            MessageOperations.TouchLaterWithoutReindex( scope, message );
        }

        return attached;
    }

    /// <summary>
    /// <c>DeleteOne#save</c>: <c>record.attachment_attachment = nil</c> destroys the current attachment (its blob purged
    /// after commit), and the destroyed attachment touches the message and its room. Assignment refreshes search text even without an attachment.
    /// Returns whether there was one.
    /// </summary>
    /// <exception cref="RecordNotFoundException">The message is gone.</exception>
    public static bool Detach( WriteScope scope, long messageId )
    {
        ArgumentNullException.ThrowIfNull( scope );

        var message = MessageQueries.Find( scope.Db, messageId );
        scope.Enlist( RecordKeys.Message( messageId ) );
        MessageOperations.ReindexAfterCommit( scope, messageId );
        if ( !AttachedOne.Remove( scope, new AttachmentOwner( Message.RecordType, message.Id ), Message.AttachmentName ) )
        {
            return false;
        }

        MessageOperations.TouchLaterWithoutReindex( scope, message );
        return true;
    }
}
