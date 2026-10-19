using Campfire.Application.Data;
using Campfire.Application.Data.Records;

using Dapper;

namespace Campfire.Application.Messages;

/// <summary>
/// The <c>action_text_rich_texts</c> row behind <c>has_rich_text :body</c>: its autosave with the message, which touches
/// the message (and so its room), and its deletion with the message.
/// </summary>
internal static class MessageBodyRows
{
    /// <summary>Inserts the body row; returns the time of the message touch it defers.</summary>
    public static DateTime Insert( WriteScope scope, Message message, string? body )
    {
        var now = scope.Now;
        var richText = new ActionTextRichText
        {
            Name = Message.BodyName,
            Body = body,
            RecordType = Message.RecordType,
            RecordId = message.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        richText.Id = DataWrites.Save( ( ) => scope.Db.Connection.ExecuteScalar<long>(
            "INSERT INTO action_text_rich_texts (body, created_at, name, record_id, record_type, updated_at) " +
            "VALUES (@Body, @CreatedAt, @Name, @RecordId, @RecordType, @UpdatedAt) RETURNING id",
            richText,
            scope.Db.Transaction ) );
        scope.Enlist( RecordKeys.RichText( richText.Id ) );
        return MessageOperations.TouchLaterWithoutReindex( scope, message );
    }

    /// <summary>
    /// <c>message.body = body</c>, saved: the row is created when the message has none, rewritten (touching the message)
    /// when its body changed, and left alone otherwise. Returns whether the row was saved.
    /// </summary>
    public static bool Update( WriteScope scope, Message message, string? body )
    {
        var richText = scope.Db.Connection.QuerySingleOrDefault<ActionTextRichText>(
            $"SELECT {Columns.RichTexts} FROM action_text_rich_texts WHERE {MessageBodies.WhereBodyOfMessage}",
            new
            {
                recordType = Message.RecordType,
                messageId = message.Id,
                name = Message.BodyName
            },
            scope.Db.Transaction );
        if ( richText is null )
        {
            Insert( scope, message, body );
            return true;
        }

        if ( richText.Body == body )
        {
            return false;
        }

        var key = RecordKeys.RichText( richText.Id );
        richText.Body = body;
        richText.UpdatedAt = scope.Now;
        DataWrites.ExpectOneRow(
            scope.Db.Connection.Execute(
                "UPDATE action_text_rich_texts SET body = @Body, updated_at = @UpdatedAt WHERE id = @Id", richText, scope.Db.Transaction ),
            key );
        scope.Enlist( key );
        MessageOperations.TouchLaterWithoutReindex( scope, message );
        return true;
    }

    /// <summary>Deletes the message's body row, as <c>message.destroy</c> destroys its <c>rich_text_body</c>.</summary>
    public static void Delete( CampfireDb db, long messageId ) =>
        db.Connection.Execute(
            $"DELETE FROM action_text_rich_texts WHERE {MessageBodies.WhereBodyOfMessage}",
            new
            {
                recordType = Message.RecordType,
                messageId,
                name = Message.BodyName
            },
            db.Transaction );
}
