using System.Globalization;
using Campfire.Application.Data;
using Campfire.Application.Data.Hooks;
using Campfire.Application.RichText;
using Campfire.Application.Security;

namespace Campfire.Web.App;

/// <summary>
/// The data layer's view of Action Text (<see cref="IMessageBodyText"/>) over <see cref="RichTextPipeline"/>:
/// <c>message.body.to_plain_text</c> for the search index, push and webhooks, and <c>body.attachables.grep(User)</c>
/// for mentions. It runs on the connection of the operation that asks (often the writer, inside its lease), so user
/// lookups go through the lookup the caller passes and nothing else touches the database.
/// </summary>
/// <remarks>
/// <see cref="RichTextException"/> fails the calling operation.
/// During message creation, that failure rolls back the transaction before external effects run.
/// </remarks>
public sealed class RichTextBodyText( SignedGlobalIds signedGlobalIds, TimeProvider clock ) : IMessageBodyText
{
    public string ToPlainText( string storedBody, Func<long, string?> userName )
    {
        ArgumentNullException.ThrowIfNull( userName );

        return RichTextPipeline.ToPlainText( storedBody, new RichTextContext( new NamesOnly( signedGlobalIds, clock, userName ), null ) );
    }

    /// <summary>
    /// Every user a verified (or, for users, unverified-fallback) SGID attaches, in document order, once each. Whether
    /// each still exists is the caller's query (<c>MessageBodies.Mentionees</c> keeps the room's members among them).
    /// </summary>
    public IReadOnlyList<long> MentionedUserIds( string storedBody )
    {
        var richTextContext = new RichTextContext( new NamesOnly( signedGlobalIds, clock, _ => "" ), null );

        return [.. RichTextPipeline.MentionedUsers( storedBody, richTextContext ).Select( user => user.Id )];
    }

    /// <summary>
    /// Plain text and mentions read only a user's id and name (<see cref="MentionUser"/>), so the rendering-only fields
    /// stay empty here.
    /// </summary>
    private sealed class NamesOnly( SignedGlobalIds signedGlobalIds, TimeProvider clock, Func<long, string?> userName ) : IAttachableResolver
    {
        public SignedAttachableLookup LocateSigned( string signedGlobalId )
        {
            if ( signedGlobalIds.Locate( signedGlobalId, SignedGlobalIds.AttachablePurpose, clock.GetUtcNow( ) ) is not { } globalId )
            {
                return SignedAttachableLookup.Invalid;
            }

            return globalId.ModelName == ModelNames.User && User( globalId.Id ) is { } user
                ? SignedAttachableLookup.ForUser( user )
                : SignedAttachableLookup.ForMissingRecord( globalId.ModelName );
        }

        private MentionUser? User( string id )
        {
            if ( !long.TryParse( id, NumberStyles.None, CultureInfo.InvariantCulture, out var userId ) || userName( userId ) is not { } name )
            {
                return null;
            }

            return new MentionUser( userId, name, "", "", "", "" );
        }
    }
}
