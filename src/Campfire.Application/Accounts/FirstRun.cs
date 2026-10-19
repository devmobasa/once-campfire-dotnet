using Campfire.Application.Data;
using Campfire.Application.Memberships;
using Campfire.Application.Rooms;
using Campfire.Application.Users;

namespace Campfire.Application.Accounts;

/// <summary><c>app/models/first_run.rb</c>: the account, the first administrator, and the first room.</summary>
public static class FirstRun
{
    /// <summary><c>FirstRun::ACCOUNT_NAME</c>.</summary>
    public const string AccountName = "Campfire";

    /// <summary><c>FirstRun::FIRST_ROOM_NAME</c>.</summary>
    public const string FirstRoomName = "All Talk";

    /// <summary>
    /// <c>FirstRun.create!(user_params)</c>, as Rails runs it:
    /// <list type="number">
    /// <item><c>Account.create!</c> in its own transaction.</item>
    /// <item><c>room.save!</c> for a new open room whose creator is the new administrator: the room joins the
    /// transaction first and autosaves the user. After commit, the room's <c>grant_access_to_all_users</c> gives the
    /// administrator a membership, then the user's callbacks run (its <c>grant_membership_to_open_rooms</c> finds it
    /// already there).</item>
    /// <item><c>room.memberships.grant_to administrator</c>, a guard and grant in one immediate transaction.</item>
    /// </list>
    /// </summary>
    /// <exception cref="RecordNotUniqueException">An account exists already.</exception>
    public static User Create( WriterLease lease, NewUser administratorAttributes )
    {
        lease.Transaction( scope => AccountOperations.Create( scope, AccountName ) );

        var (administrator, room) = lease.Transaction( scope =>
        {
            var administrator = UserOperations.Insert( scope, administratorAttributes with
            {
                Role = UserRole.Administrator
            } );
            var room = RoomOperations.Insert( scope, RoomType.Open, FirstRoomName, administrator.Id );
            RoomOperations.RegisterSaveCallbacks( scope, room, typeChangedToOpen: true );
            UserOperations.RegisterCreated( scope, administrator, administratorAttributes );
            return (administrator, room);
        } );

        MembershipGrants.GrantTo( lease.Db, room.Id, room.DefaultInvolvement, [administrator.Id] );
        return administrator;
    }
}
