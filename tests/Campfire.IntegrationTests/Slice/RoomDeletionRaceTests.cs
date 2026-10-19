using System.Net;
using Campfire.Application.Data;
using Campfire.Application.Rooms;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

public sealed class RoomDeletionRaceTests
{
    [Fact]
    public async Task A_patch_authorized_before_delete_cannot_reopen_the_logically_removed_room( )
    {
        using var factory = new SliceAppFactory( );
        var roomId = SliceAppFactory.Id( "watercooler" );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( roomId ) );
        using var ownedClient = client;

        var reply = await DestroyWhileTheWriteWaitsAsync( factory, roomId, ( ) => client.SubmitFormAsync( HttpMethod.Patch, Routes.RoomsOpen( roomId ), token,
            [("room[name]", "reopened after delete")] ) );

        Assert.Equal( HttpStatusCode.NotFound, reply.Status );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT COUNT(*) FROM memberships WHERE room_id={roomId}" ) );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={roomId} AND type='Rooms::Open'" ) );
        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={roomId}" ) == 0 );
    }

    [Theory]
    [InlineData( "post", HttpStatusCode.OK )]
    [InlineData( "edit", HttpStatusCode.NotFound )]
    [InlineData( "boost", HttpStatusCode.NotFound )]
    public async Task A_message_write_authorized_before_delete_is_rejected_after_logical_removal( string write, HttpStatusCode status )
    {
        using var factory = new SliceAppFactory( );
        var roomId = SliceAppFactory.Id( "designers" );
        var messageId = SliceAppFactory.Id( "first" );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( roomId ) );
        using var ownedClient = client;
        Func<Task<SliceReply>> send = write switch
        {
            "post" => ( ) => client.PostMessageAsync( roomId, token, body: "<p>after delete</p>", clientMessageId: "after-delete" ),
            "edit" => ( ) => client.SubmitFormAsync( HttpMethod.Patch, Routes.RoomMessage( roomId, messageId ), token, [("message[body]", "after delete")] ),
            _ => ( ) => client.SubmitFormAsync( HttpMethod.Post, Routes.MessageBoosts( messageId ), token, [("boost[content]", "after delete")], accept: "text/html" ),
        };

        var reply = await DestroyWhileTheWriteWaitsAsync( factory, roomId, send );

        Assert.Equal( status, reply.Status );
        if ( write == "post" )
        {
            // MessagesController#create rescues RecordNotFound with render action: :room_not_found.
            Assert.Contains( "This room was deleted.", reply.Body, StringComparison.Ordinal );
        }

        // The deletion destroys the room's messages later, so these counts look only for the content of the stale write.
        Assert.Equal( 0, SliceRows.MessageCount( factory, "after-delete" ) );
        Assert.Equal( 0, factory.Scalar<long>( "SELECT COUNT(*) FROM action_text_rich_texts WHERE body LIKE '%after delete%'" ) );
        Assert.Equal( 0, factory.Scalar<long>( "SELECT COUNT(*) FROM boosts WHERE content='after delete'" ) );
    }

    /// <summary>
    /// Holds an external immediate transaction until the request waits for the writer, then destroys the room inside it. The
    /// authorization reads of the request finish first, so its write is stale.
    /// </summary>
    private static async Task<SliceReply> DestroyWhileTheWriteWaitsAsync( SliceAppFactory factory, long roomId, Func<Task<SliceReply>> send )
    {
        var database = factory.App.Database;
        using var deleting = CampfireDatabase.Open( new DatabaseOptions { StorageRoot = factory.StoragePath, Environment = "production" }, database.Clock, database.Hooks );
        using var writer = await deleting.AcquireWriterAsync( TestContext.Current.CancellationToken );
        var locked = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
        var remove = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
        var deletion = Task.Run( ( ) => writer.Transaction( scope =>
        {
            locked.SetResult( );
            remove.Task.GetAwaiter( ).GetResult( );
            RoomOperations.Destroy( scope, roomId );
        } ), TestContext.Current.CancellationToken );
        await locked.Task.WaitAsync( TestContext.Current.CancellationToken );
        try
        {
            // The external immediate transaction blocks the write of the request, while its authorization reads still finish.
            var request = send( );
            await SliceWait.UntilAsync( async ( ) =>
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource( TestContext.Current.CancellationToken );
                probe.CancelAfter( TimeSpan.FromMilliseconds( 100 ) );
                try
                {
                    using var lease = await database.AcquireWriterAsync( probe.Token );
                    return false;
                }
                catch ( OperationCanceledException ) when ( !TestContext.Current.CancellationToken.IsCancellationRequested )
                {
                    return true;
                }
            }, value => value );
            remove.SetResult( );
            await deletion;
            return await request;
        }
        finally
        {
            remove.TrySetResult( );
            await deletion;
        }
    }
}
