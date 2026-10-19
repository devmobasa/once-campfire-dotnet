using Campfire.Web.Cable;
using Campfire.Web.Channels;
using Campfire.Web.Workers;

namespace Campfire.Web.App;

/// <summary>
/// Every controller, channel and job kind of the app, one line each (<c>docs/development.md</c>). Nothing is found by
/// reflection, so the app stays Native AOT compatible: a new controller, channel or job adds its line here, in the
/// section's alphabetical order.
/// </summary>
public static class AppRegistrations
{
    /// <summary>The app's channels, by the Ruby class name clients subscribe with.</summary>
    public static CableBuilder AddAppChannels( this CableBuilder cable )
    {
        ArgumentNullException.ThrowIfNull( cable );

        cable.AddChannel<PresenceChannel>( PresenceChannel.ClassName );
        cable.AddChannel<ReadRoomsChannel>( ReadRoomsChannel.ClassName );
        cable.AddChannel<RoomChannel>( RoomChannel.ClassName );
        cable.AddChannel<RoomMessagesChannel>( RoomMessagesChannel.ClassName );
        cable.AddChannel<TypingNotificationsChannel>( TypingNotificationsChannel.ClassName );
        cable.AddChannel<UnreadRoomsChannel>( UnreadRoomsChannel.ClassName );

        return cable;
    }

    /// <summary>
    /// The job kinds for background work, each with its handler: <paramref name="jobConcurrency"/> (<c>JOB_CONCURRENCY</c>)
    /// consumers per Active Job kind, one for purges and banned-content removal. Kinds whose jobs only notify drop when
    /// full; kinds whose loss would leave data behind wait for room.
    /// </summary>
    public static IServiceCollection AddAppJobs( this IServiceCollection services, int jobConcurrency )
    {
        ArgumentNullException.ThrowIfNull( services );

        const int capacity = WorkerServiceCollectionExtensions.JobCapacity;
        var minute = TimeSpan.FromMinutes( 1 );
        var minutes = TimeSpan.FromMinutes( 5 );
        services.AddJobKind<AnalyzeBlobJob, AnalyzeBlobHandler>( new( "ActiveStorage::AnalyzeJob", capacity, jobConcurrency, FullQueuePolicy.Wait, minutes ) );
        services.AddJobKind<BroadcastUnreadRoomJob, BroadcastUnreadRoomHandler>( new( "Message::BroadcastUnreadRoomJob", capacity, 1, FullQueuePolicy.Drop, minute ) );
        services.AddJobKind<PurgeBlobJob, PurgeBlobHandler>( new( "ActiveStorage::PurgeJob", capacity, 1, FullQueuePolicy.Wait, minutes ) );
        services.AddJobKind<PushMessageJob, PushMessageHandler>( new( "Room::PushMessageJob", capacity, jobConcurrency, FullQueuePolicy.Drop, minute ) );
        services.AddJobKind<RemoveBannedContentJob, RemoveBannedContentHandler>( new( "RemoveBannedContentJob", capacity, 1, FullQueuePolicy.Wait, minutes ) );
        services.AddJobKind<WebhookJob, WebhookHandler>( new( "Bot::WebhookJob", capacity, jobConcurrency, FullQueuePolicy.Drop, minutes ) );

        return services;
    }
}
