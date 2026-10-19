using System.Globalization;
using Campfire.Application.Data;
using Campfire.Web.Views.Helpers;
using Campfire.Web.Views.Models;

namespace Campfire.Web.Views.Caching;

/// <summary>A record's identity and version: the parts of <c>cache_key_with_version</c>.</summary>
/// <param name="Id">The record id.</param>
/// <param name="UpdatedAt">The record's <c>updated_at</c>.</param>
public readonly record struct RecordVersion( long Id, DateTimeOffset UpdatedAt )
{
    /// <summary><c>record.cache_key_with_version</c>: <c>"messages/1-20240601120000000000"</c>.</summary>
    public string CacheKey( string table ) =>
        string.Create( CultureInfo.InvariantCulture, $"{table}/{Id}-{RubyText.UsecTimestamp( UpdatedAt )}" );
}

/// <summary>
/// Inputs for a <c>messages/_message</c> fragment, all known before its view model is built.
/// Rails keys the fragment on the message alone (<c>cache [ message, "presentation-v3" ]</c>; boosts touch the
/// message); this key also covers what Rails lets go stale: the author's and boosters' names and avatars, the
/// room's display name, and the origin in the copy-link URL.
/// </summary>
/// <param name="Message">The message's id and <c>updated_at</c>.</param>
/// <param name="Creator">The author's id and <c>updated_at</c> (name, bio and avatar changes touch it).</param>
/// <param name="RoomName"><c>room_display_name(message.room, for_user: nil)</c>.</param>
/// <param name="Boosters">Each boost's and booster's version, in <c>boosts.ordered</c> order.</param>
/// <param name="PublicBaseUrl">The origin the fragment's absolute URLs use.</param>
public readonly record struct MessageCacheInputs(
    RecordVersion Message,
    RecordVersion Creator,
    string RoomName,
    IReadOnlyList<(RecordVersion Boost, RecordVersion Booster)> Boosters,
    string PublicBaseUrl )
{
    /// <summary>Request-owned memoization; current dependencies are checked on every use, including record copies.</summary>
    internal FragmentKeys.MessageDependencyMemo? DependencyMemo { get; init; }
}

/// <summary>
/// Fragment keys, shaped after <c>ActionView::Helpers::CacheHelper#fragment_name_with_digest</c>:
/// <c>views/&lt;template&gt;/&lt;record cache_key_with_version&gt;/...</c>. Rails adds a digest of the template tree
/// because Redis outlives deploys; this store lives and dies with the process, so a template change can never meet
/// an old fragment. The inputs that aren't the record itself go in as a SHA-256 digest.
/// </summary>
public static partial class FragmentKeys
{
    /// <summary>The version the message partial's cache call names; bump it with the presentation filters.</summary>
    public const string PresentationVersion = "presentation-v3";

    /// <summary>The message fragment's key (<see cref="WriteMessageKey"/>) as a string.</summary>
    public static string Message( MessageCacheInputs inputs )
    {
        Span<char> key = stackalloc char[MessageKeyCapacity];
        return new string( key[..WriteMessageKey( inputs, key )] );
    }

    /// <summary>
    /// <c>messages/boosts/_boost</c>'s <c>cache boost</c>, plus the booster as the fragment shows it. The fragment has
    /// only relative URLs, so no origin is part of the key: the fragment a boost's broadcast renders is the one the
    /// boosts list serves next, as in Rails.
    /// </summary>
    public static string Boost( BoostView boost )
    {
        ArgumentNullException.ThrowIfNull( boost );

        var version = new RecordVersion( boost.Id, boost.UpdatedAt ).CacheKey( TableNames.Boosts );
        var booster = boost.Booster;
        var avatarVersion = booster.Version is { } updatedAt
            ? new RecordVersion( booster.Id, updatedAt ).CacheKey( TableNames.Users )
            : booster.AvatarPath;
        var parts = string.Join(
            '\n',
            boost.Content,
            boost.AllEmoji ? "1" : "0",
            booster.Id.ToString( CultureInfo.InvariantCulture ),
            booster.Name,
            booster.Title,
            avatarVersion );
        return $"views/messages/boosts/_boost/{version}/{Digest( parts )}";
    }

    /// <summary>
    /// <c>users/sidebars/rooms/_direct</c>'s <c>cache membership</c>, plus the room (its <c>updated_at</c> is in the
    /// fragment) and the members whose names and avatars it shows.
    /// </summary>
    public static string SidebarDirect( RecordVersion membership, RecordVersion room, IReadOnlyList<RecordVersion> members )
    {
        ArgumentNullException.ThrowIfNull( members );

        Span<char> key = stackalloc char[SidebarDirectKeyCapacity];
        return new string( key[..WriteSidebarDirectKey( membership, room, members, key )] );
    }

    /// <summary>
    /// <c>messages/_message.json.jbuilder</c>'s <c>json.cache! message</c> (Jbuilder expands keys under <c>jbuilder/</c>),
    /// plus the creator's version (the nested <c>users/_user</c> fragment) and the base URL the fragment's URLs use.
    /// </summary>
    public static string MessageJson( RecordVersion message, RecordVersion creator, string baseUrl ) =>
        $"jbuilder/views/messages/_message/{message.CacheKey( TableNames.Messages )}/{Digest( creator.CacheKey( TableNames.Users ) + "\n" + baseUrl )}";

    /// <summary><c>messages/boosts/_boost.json.jbuilder</c>'s <c>json.cache! boost</c>, plus the booster and the base URL.</summary>
    public static string BoostJson( RecordVersion boost, RecordVersion booster, string baseUrl ) =>
        $"jbuilder/views/messages/boosts/_boost/{boost.CacheKey( TableNames.Boosts )}/{Digest( booster.CacheKey( TableNames.Users ) + "\n" + baseUrl )}";

    /// <summary><c>users/_user.json.jbuilder</c>'s <c>json.cache! user</c>, plus the base URL of its avatar URL.</summary>
    public static string UserJson( RecordVersion user, string baseUrl ) =>
        $"jbuilder/views/users/_user/{user.CacheKey( TableNames.Users )}/{Digest( baseUrl )}";

    /// <summary>The digest of <paramref name="text"/> (<see cref="WriteDigest"/>) as a string.</summary>
    private static string Digest( string text )
    {
        Span<char> digest = stackalloc char[DigestChars];
        WriteDigest( text, digest );
        return new string( digest );
    }
}
