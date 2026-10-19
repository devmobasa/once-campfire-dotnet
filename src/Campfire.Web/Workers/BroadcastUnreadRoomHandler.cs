using System.Runtime.CompilerServices;
using Campfire.Application.Memberships;
using Campfire.Application.Messages;
using Campfire.Web.App;

namespace Campfire.Web.Workers;

/// <summary>Publishes unread room notices through the bounded, single-consumer notification queue.</summary>
public sealed class BroadcastUnreadRoomHandler( CampfireApp app ) : IJobHandler<BroadcastUnreadRoomJob>
{
    public async Task PerformAsync( BroadcastUnreadRoomJob job, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( job );

        var message = await app.Database.ReadAsync( db => MessageQueries.Find( db, job.MessageId ), cancellationToken );
        var now = app.Now.UtcDateTime;
        await app.Broadcasts.UnreadRoomAsync( message.RoomId, message.CreatedAt, Recipients( cancellationToken ), cancellationToken );

        async IAsyncEnumerable<long> Recipients( [EnumeratorCancellation] CancellationToken token )
        {
            long afterMembershipId = 0;
            while ( true )
            {
                token.ThrowIfCancellationRequested( );
                var page = await app.Database.ReadAsync(
                    db => MembershipQueries.UnreadOrConnectedPage( db, message.RoomId, now, afterMembershipId ), token );
                if ( page.Count == 0 )
                {
                    yield break;
                }

                foreach ( var recipient in page )
                {
                    token.ThrowIfCancellationRequested( );
                    yield return recipient.UserId;
                }

                afterMembershipId = page[^1].MembershipId;
            }
        }
    }
}
