using Campfire.Application.Serialization;
using Campfire.Web.Views.Caching;

namespace Campfire.Web.Views.Json;

/// <summary><c>users/_user.json.jbuilder</c>: <c>json.(user, :id, :name, :role)</c> then <c>avatar_url</c>.</summary>
/// <param name="Id">The user's id.</param>
/// <param name="Name">The user's name.</param>
/// <param name="Role">"member", "administrator" or "bot".</param>
/// <param name="AvatarUrl"><c>fresh_user_avatar_url(user)</c>: absolute.</param>
public sealed record UserJson( long Id, string Name, string Role, string AvatarUrl );

/// <summary><c>messages/_message.json.jbuilder</c>.</summary>
/// <param name="Id">The message's id.</param>
/// <param name="CreatedAt"><c>message.created_at.utc</c>.</param>
/// <param name="PlainText"><c>message.plain_text_body</c>.</param>
/// <param name="Html"><c>message.body.to_s</c>: the rich text rendered with its layout.</param>
/// <param name="Creator">The author (<c>users/_user</c>).</param>
/// <param name="RoomId"><c>message.room_id</c>.</param>
/// <param name="Url"><c>room_message_url(message.room, message)</c>.</param>
public sealed record MessageJson( long Id, DateTimeOffset CreatedAt, string PlainText, string Html, UserJson Creator, long RoomId, string Url );

/// <summary><c>messages/boosts/_boost.json.jbuilder</c>.</summary>
/// <param name="Id">The boost's id.</param>
/// <param name="Content">The boost text.</param>
/// <param name="CreatedAt"><c>boost.created_at.utc</c>.</param>
/// <param name="Booster">Who boosted (<c>users/_user</c>).</param>
/// <param name="MessageId"><c>boost.message_id</c>.</param>
/// <param name="MessageUrl"><c>room_message_url(boost.message.room, boost.message)</c>.</param>
public sealed record BoostJson( long Id, string Content, DateTimeOffset CreatedAt, UserJson Booster, long MessageId, string MessageUrl );

/// <summary><c>autocompletable/users/_user.json.jbuilder</c>.</summary>
/// <param name="Name"><c>h(user.name)</c>: HTML-escaped before JSON encoding.</param>
/// <param name="Value"><c>user.id</c>.</param>
/// <param name="AvatarUrl"><c>fresh_user_avatar_url(user)</c>: absolute.</param>
/// <param name="SignedGlobalId"><c>user.attachable_sgid</c>.</param>
public sealed record AutocompletableUserJson( string Name, long Value, string AvatarUrl, string SignedGlobalId )
{
    /// <summary>The partial's value for a user: the name goes through <c>h</c>, as the template calls it.</summary>
    public static AutocompletableUserJson For( string name, long id, string avatarUrl, string signedGlobalId ) =>
        new( Escaping.Escape( name ), id, avatarUrl, signedGlobalId );
}

/// <summary>
/// A Jbuilder partial whose body is <c>json.cache! record do ... end</c>, on its way into a view: with a key, the
/// store is checked first. <see cref="Build"/> runs only on a miss. Without a store, it renders
/// uncached (<c>perform_caching = false</c>).
/// </summary>
/// <typeparam name="T">The partial's value.</typeparam>
public sealed record JsonEntry<T>( string? CacheKey, Func<T> Build )
    where T : class;

/// <summary>Factories for <see cref="JsonEntry{T}"/>.</summary>
public static class JsonEntry
{
    /// <summary>An uncached value.</summary>
    public static JsonEntry<T> Of<T>( T value )
        where T : class => new( null, ( ) => value );
}

/// <summary>
/// The Jbuilder views. Jbuilder's <c>target!</c> goes through <c>to_json</c>, so the output is ActiveSupport's
/// encoding (<c>Campfire.Application.Serialization</c>): <c>&lt; &gt; &amp;</c> as <c>< > &</c>, U+2028/U+2029 raw,
/// times as <c>xmlschema(3)</c>. A cached fragment is stored as its JSON before that escaping pass and inserted
/// verbatim, as ActiveSupport escapes the whole document, fragments included.
/// </summary>
public static class JbuilderViews
{
    /// <summary><c>users/_user.json.jbuilder</c> on its own (<c>json.cache! user</c>).</summary>
    public static string User( JsonEntry<UserJson> user, FragmentCache? cache = null )
    {
        ArgumentNullException.ThrowIfNull( user );

        using var writer = new JsonPayloadWriter( );
        WriteEntry( writer, user, cache, WriteUser );
        return writer.ToString( );
    }

    /// <summary><c>messages/by_bots/index.json.jbuilder</c>: <c>json.array! @messages, partial: "messages/message"</c>.</summary>
    public static string MessagesByBotsIndex( IEnumerable<JsonEntry<MessageJson>> messages, FragmentCache? cache = null )
    {
        ArgumentNullException.ThrowIfNull( messages );

        using var writer = new JsonPayloadWriter( );
        writer.WriteStartArray( );
        foreach ( var message in messages )
        {
            WriteEntry( writer, message, cache, WriteMessage );
        }

        writer.WriteEndArray( );
        return writer.ToString( );
    }

    /// <summary><c>messages/by_bots/show.json.jbuilder</c>: <c>json.partial! "messages/message"</c>.</summary>
    public static string MessagesByBotsShow( JsonEntry<MessageJson> message, FragmentCache? cache = null )
    {
        ArgumentNullException.ThrowIfNull( message );

        using var writer = new JsonPayloadWriter( );
        WriteEntry( writer, message, cache, WriteMessage );
        return writer.ToString( );
    }

    /// <summary><c>messages/boosts/by_bots/show.json.jbuilder</c>: <c>json.partial! "messages/boosts/boost"</c>.</summary>
    public static string BoostsByBotsShow( JsonEntry<BoostJson> boost, FragmentCache? cache = null )
    {
        ArgumentNullException.ThrowIfNull( boost );

        using var writer = new JsonPayloadWriter( );
        WriteEntry( writer, boost, cache, WriteBoost );
        return writer.ToString( );
    }

    /// <summary><c>autocompletable/users/index.json.jbuilder</c>: the <c>_user</c> partial per user (not cached).</summary>
    public static string AutocompletableUsersIndex( IEnumerable<AutocompletableUserJson> users )
    {
        ArgumentNullException.ThrowIfNull( users );

        using var writer = new JsonPayloadWriter( );
        writer.WriteStartArray( );
        foreach ( var user in users )
        {
            writer.WriteStartObject( );
            Property( writer, "name", user.Name );
            writer.WritePropertyName( "value" );
            writer.WriteNumber( user.Value );
            Property( writer, "avatar_url", user.AvatarUrl );
            Property( writer, "sgid", user.SignedGlobalId );
            writer.WriteEndObject( );
        }

        writer.WriteEndArray( );
        return writer.ToString( );
    }

    /// <summary>
    /// <c>json.cache! key do ... end</c>: the stored fragment when there is one, else the built value, stored as its
    /// JSON before the HTML-entity pass.
    /// </summary>
    private static void WriteEntry<T>( JsonPayloadWriter writer, JsonEntry<T> entry, FragmentCache? cache, Action<JsonPayloadWriter, T> write )
        where T : class
    {
        if ( cache is null || entry.CacheKey is not { } key )
        {
            write( writer, entry.Build( ) );
            return;
        }

        var fragment = cache.FetchValue(
            key,
            ( ) =>
            {
                using var unescaped = new JsonPayloadWriter( JsonEscaping.None );
                write( unescaped, entry.Build( ) );
                return unescaped.ToString( );
            },
            json => json.Length );
        writer.WriteRawValue( fragment );
    }

    /// <summary><c>users/_user.json.jbuilder</c>'s body.</summary>
    private static void WriteUser( JsonPayloadWriter writer, UserJson user )
    {
        writer.WriteStartObject( );
        writer.WritePropertyName( "id" );
        writer.WriteNumber( user.Id );
        Property( writer, "name", user.Name );
        Property( writer, "role", user.Role );
        Property( writer, "avatar_url", user.AvatarUrl );
        writer.WriteEndObject( );
    }

    /// <summary><c>messages/_message.json.jbuilder</c>'s body. The creator's own <c>json.cache! user</c> is covered by
    /// the message fragment's key, which names the creator's version (<see cref="FragmentKeys.MessageJson"/>).</summary>
    private static void WriteMessage( JsonPayloadWriter writer, MessageJson message )
    {
        writer.WriteStartObject( );
        writer.WritePropertyName( "id" );
        writer.WriteNumber( message.Id );
        writer.WritePropertyName( "created_at" );
        writer.WriteTime( message.CreatedAt.ToUniversalTime( ) );
        writer.WritePropertyName( "body" );
        writer.WriteStartObject( );
        Property( writer, "plain_text", message.PlainText );
        Property( writer, "html", message.Html );
        writer.WriteEndObject( );
        writer.WritePropertyName( "creator" );
        WriteUser( writer, message.Creator );
        writer.WritePropertyName( "room" );
        writer.WriteStartObject( );
        writer.WritePropertyName( "id" );
        writer.WriteNumber( message.RoomId );
        writer.WriteEndObject( );
        Property( writer, "url", message.Url );
        writer.WriteEndObject( );
    }

    /// <summary><c>messages/boosts/_boost.json.jbuilder</c>'s body.</summary>
    private static void WriteBoost( JsonPayloadWriter writer, BoostJson boost )
    {
        writer.WriteStartObject( );
        writer.WritePropertyName( "id" );
        writer.WriteNumber( boost.Id );
        Property( writer, "content", boost.Content );
        writer.WritePropertyName( "created_at" );
        writer.WriteTime( boost.CreatedAt.ToUniversalTime( ) );
        writer.WritePropertyName( "booster" );
        WriteUser( writer, boost.Booster );
        writer.WritePropertyName( "message" );
        writer.WriteStartObject( );
        writer.WritePropertyName( "id" );
        writer.WriteNumber( boost.MessageId );
        Property( writer, "url", boost.MessageUrl );
        writer.WriteEndObject( );
        writer.WriteEndObject( );
    }

    private static void Property( JsonPayloadWriter writer, string name, string value )
    {
        writer.WritePropertyName( name );
        writer.WriteString( value );
    }
}
