using Campfire.Application.Storage;

namespace Campfire.Application.Tests.Storage;

public sealed class VariantFlightsTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Identical_keys_wait_while_distinct_blobs_and_variations_proceed( )
    {
        using var flights = new VariantFlights( );
        using var owner = await flights.EnterAsync( 1, "a", Cancel );
        var same = flights.EnterAsync( 1, "a", Cancel ).AsTask( );
        Assert.False( same.IsCompleted );
        using var anotherBlob = await flights.EnterAsync( 2, "a", Cancel );
        using var anotherVariation = await flights.EnterAsync( 1, "b", Cancel );

        owner.Dispose( );
        using var resumed = await same;
    }

    [Fact]
    public async Task Full_capacity_still_admits_existing_keys_and_retired_keys_free_space( )
    {
        using var flights = new VariantFlights( );
        using var occupied = await OccupyOtherKeysAsync( flights );
        using var owner = await flights.EnterAsync( 1, "a", Cancel );
        var overflow = flights.EnterAsync( 2, "b", Cancel ).AsTask( );
        var same = flights.EnterAsync( 1, "a", Cancel ).AsTask( );
        Assert.False( overflow.IsCompleted );

        owner.Dispose( );
        using var resumed = await same;
        Assert.False( overflow.IsCompleted );
        resumed.Dispose( );
        using var admitted = await overflow;
    }

    [Fact]
    public async Task Cancelling_waiters_preserves_the_owner_and_other_waiters( )
    {
        using var flights = new VariantFlights( );
        using var occupied = await OccupyOtherKeysAsync( flights );
        using var owner = await flights.EnterAsync( 1, "a", Cancel );
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource( Cancel );
        var sameCancelled = flights.EnterAsync( 1, "a", stopping.Token ).AsTask( );
        var admissionCancelled = flights.EnterAsync( 2, "b", stopping.Token ).AsTask( );
        var peer = flights.EnterAsync( 1, "a", Cancel ).AsTask( );

        await stopping.CancelAsync( );
        await Assert.ThrowsAnyAsync<OperationCanceledException>( ( ) => sameCancelled );
        await Assert.ThrowsAnyAsync<OperationCanceledException>( ( ) => admissionCancelled );
        Assert.False( peer.IsCompleted );
        owner.Dispose( );
        using var resumed = await peer;
    }

    [Fact]
    public async Task Churn_reuses_capacity_after_every_distinct_key_retires( )
    {
        using var flights = new VariantFlights( );
        for ( var index = 0; index < 256; index++ )
        {
            using var owner = await flights.EnterAsync( index, "a", Cancel );
            using var distinct = await flights.EnterAsync( index, "b", Cancel );
            var queued = flights.EnterAsync( index, "a", Cancel ).AsTask( );
            Assert.False( queued.IsCompleted );
            owner.Dispose( );
            using var resumed = await queued;
        }
    }

    [Fact]
    public async Task Disposal_wakes_admission_and_preserves_borrowed_gate_lifetimes( )
    {
        using var flights = new VariantFlights( );
        using var occupied = await OccupyOtherKeysAsync( flights );
        using var owner = await flights.EnterAsync( 1, "a", Cancel );
        var admission = flights.EnterAsync( 2, "b", Cancel ).AsTask( );
        var same = flights.EnterAsync( 1, "a", Cancel ).AsTask( );

        flights.Dispose( );
        await Assert.ThrowsAsync<ObjectDisposedException>( ( ) => admission );
        await Assert.ThrowsAsync<ObjectDisposedException>( async ( ) => await flights.EnterAsync( 3, "c", Cancel ) );
        owner.Dispose( );
        owner.Dispose( );
        await Assert.ThrowsAsync<ObjectDisposedException>( ( ) => same );
    }

    private static async Task<OccupiedKeys> OccupyOtherKeysAsync( VariantFlights flights )
    {
        var keys = new OccupiedKeys( );
        try
        {
            for ( var index = 0; index < 63; index++ )
            {
                keys.Leases.Add( await flights.EnterAsync( 1000 + index, "occupied", Cancel ) );
            }

            return keys;
        }
        catch
        {
            keys.Dispose( );
            throw;
        }
    }

    private sealed class OccupiedKeys : IDisposable
    {
        public List<IDisposable> Leases { get; } = [];

        public void Dispose( )
        {
            foreach ( var lease in Leases )
            {
                lease.Dispose( );
            }
        }
    }
}
