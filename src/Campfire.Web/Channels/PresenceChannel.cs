using System.Text.Json.Nodes;
using Campfire.Application.Data;
using Campfire.Application.Memberships;
using Campfire.Web.App;

namespace Campfire.Web.Channels;

/// <summary>
/// <c>PresenceChannel &lt; RoomChannel</c> (<c>app/channels/presence_channel.rb</c>): <c>on_subscribe :present</c> marks
/// the membership connected and read and tells the user's other tabs (<c>user_&lt;id&gt;_reads</c>);
/// <c>on_unsubscribe :absent</c> disconnects it; <c>refresh</c> keeps it connected (<c>Membership::Connectable</c>,
/// 60 s TTL). Neither callback runs for a rejected subscription.
/// </summary>
/// <remarks>
/// <c>present</c>, <c>absent</c> and <c>refresh</c> are public, so they are all actions: the presence controller
/// (<c>app/javascript/controllers/presence_controller.js</c>) performs <c>absent</c> when its tab has been hidden for
/// 5 s, <c>present</c> when it is visible again, and <c>refresh</c> every 50 s. The inherited <c>subscribed</c> is an
/// action as well (<see cref="RoomChannel"/>).
/// </remarks>
public sealed class PresenceChannel( CampfireApp app ) : RoomChannel( app )
{
    /// <summary>The Ruby class name clients subscribe with (<c>PresenceChannel</c>).</summary>
    public new const string ClassName = "PresenceChannel";

    private const string PresentAction = "present";

    private const string AbsentAction = "absent";

    private const string RefreshAction = "refresh";

    public override async ValueTask SubscribedAsync( CancellationToken cancellationToken )
    {
        await base.SubscribedAsync( cancellationToken );

        if ( !IsRejected )
        {
            await PresentAsync( cancellationToken );
        }
    }

    public override async ValueTask UnsubscribedAsync( CancellationToken cancellationToken )
    {
        if ( !IsRejected )
        {
            await AbsentAsync( cancellationToken );
        }
    }

    public override async ValueTask<bool> PerformAsync( string action, JsonObject data, CancellationToken cancellationToken )
    {
        switch ( action )
        {
            case PresentAction:
                await PresentAsync( cancellationToken );
                return true;
            case AbsentAction:
                await AbsentAsync( cancellationToken );
                return true;
            case RefreshAction:
                var membershipId = await MembershipIdAsync( RefreshAction, cancellationToken );
                await UpdateMembershipAsync(
                    scope => MembershipPresence.RefreshConnection( scope, membershipId ),
                    cancellationToken );
                return true;
            default:
                return await base.PerformAsync( action, data, cancellationToken );
        }
    }

    /// <summary><c>present</c>: <c>membership.present</c>, then <c>broadcast_read_room</c>.</summary>
    private async Task PresentAsync( CancellationToken cancellationToken )
    {
        var membershipId = await MembershipIdAsync( PresentAction, cancellationToken );
        var updated = await UpdateMembershipAsync( scope => MembershipPresence.Present( scope, membershipId ), cancellationToken );

        // broadcast_read_room finds the membership again for its room_id (which is @room's id): gone, it raises.
        if ( !updated )
        {
            throw new InvalidOperationException(
                "undefined method 'room_id' for nil (PresenceChannel#broadcast_read_room: the membership is gone)" );
        }

        App.Broadcasts.ReadRoom( CurrentUser.Id, Room!.Id, App.Now );
    }

    /// <summary><c>absent</c>: <c>membership.disconnected</c>.</summary>
    private async Task AbsentAsync( CancellationToken cancellationToken )
    {
        var membershipId = await MembershipIdAsync( AbsentAction, cancellationToken );
        await UpdateMembershipAsync( scope => MembershipPresence.Disconnected( scope, membershipId ), cancellationToken );
    }

    /// <summary>
    /// Runs a <c>Membership::Connectable</c> update on the membership found a moment ago. Rails updates the record it
    /// holds by id, so when the row was deleted in between (the membership revoked, the user deactivated) the
    /// <c>UPDATE</c> matches nothing and nothing is raised. The Application operations load the row inside the write,
    /// where the same race surfaces as <see cref="RecordNotFoundException"/>: that is the no-op, reported as false.
    /// </summary>
    private async Task<bool> UpdateMembershipAsync( Action<WriteScope> update, CancellationToken cancellationToken )
    {
        try
        {
            await App.Database.WriteAsync( update, cancellationToken );
            return true;
        }
        catch ( RecordNotFoundException )
        {
            return false;
        }
    }

    /// <summary>
    /// <c>@room.memberships.find_by(user: current_user)</c>. Once the membership is gone it is <c>nil</c>, and calling
    /// the connection method on it raises <c>NoMethodError</c>: the command is logged and nothing is sent (a subscribe
    /// is then neither confirmed nor rejected). The membership's removal disconnects the socket anyway
    /// (<c>after_destroy_commit { user.reset_remote_connections }</c>).
    /// </summary>
    private async Task<long> MembershipIdAsync( string method, CancellationToken cancellationToken )
    {
        if ( Room is not { } room )
        {
            throw new InvalidOperationException( $"undefined method 'memberships' for nil (PresenceChannel#{method} without a room)" );
        }

        var userId = CurrentUser.Id;
        var membership = await App.Database.ReadAsync(
            db => MembershipQueries.FindByRoomAndUser( db, room.Id, userId ),
            cancellationToken );
        if ( membership is null )
        {
            throw new InvalidOperationException( $"undefined method '{method}' for nil (PresenceChannel: the membership is gone)" );
        }

        return membership.Id;
    }
}
