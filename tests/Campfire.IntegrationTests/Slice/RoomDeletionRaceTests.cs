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
            // The external immediate transaction blocks PATCH's write, while its authorization reads still finish.
            var patch = client.SubmitFormAsync( HttpMethod.Patch, Routes.RoomsOpen( roomId ), token,
                [("room[name]", "reopened after delete")] );
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
            var reply = await patch;

            Assert.Equal( HttpStatusCode.NotFound, reply.Status );
            Assert.Equal( 0, factory.Scalar<long>( $"SELECT COUNT(*) FROM memberships WHERE room_id={roomId}" ) );
            Assert.Equal( 0, factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={roomId} AND type='Rooms::Open'" ) );
            await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={roomId}" ) == 0 );
        }
        finally
        {
            remove.TrySetResult( );
            await deletion;
        }
    }
}
