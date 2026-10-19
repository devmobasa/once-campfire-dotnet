namespace Campfire.Application.Storage;

/// <summary>Bounds active variant keys and serializes their producers until every borrower leaves.</summary>
internal sealed class VariantFlights : IDisposable
{
    private readonly Lock _lock = new( );
    private readonly Dictionary<(long BlobId, string Digest), Flight> _flights = [];
    private const int MaximumFlights = 64;
    private TaskCompletionSource _changed = NewSignal( );
    private bool _disposed;

    public void ThrowIfDisposed( )
    {
        lock ( _lock )
        {
            ObjectDisposedException.ThrowIf( _disposed, this );
        }
    }

    public async ValueTask<IDisposable> EnterAsync( long blobId, string digest, CancellationToken cancellationToken )
    {
        var key = (blobId, digest);
        while ( true )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            Flight? flight;
            Task changed;
            lock ( _lock )
            {
                ObjectDisposedException.ThrowIf( _disposed, this );
                if ( !_flights.TryGetValue( key, out flight ) && _flights.Count < MaximumFlights )
                {
                    flight = new Flight( );
                    _flights.Add( key, flight );
                }

                if ( flight is not null )
                {
                    flight.References++;
                }

                changed = _changed.Task;
            }

            if ( flight is null )
            {
                await changed.WaitAsync( cancellationToken );
                continue;
            }

            var entered = false;
            try
            {
                await flight.Gate.WaitAsync( cancellationToken );
                entered = true;
                ThrowIfDisposed( );
                return new Lease( this, key, flight );
            }
            catch
            {
                Release( key, flight, entered );
                throw;
            }
        }
    }

    public void Dispose( )
    {
        lock ( _lock )
        {
            _disposed = true;
            _changed.TrySetResult( );
        }
    }

    private void Release( (long BlobId, string Digest) key, Flight flight, bool entered )
    {
        lock ( _lock )
        {
            if ( entered )
            {
                flight.Gate.Release( );
            }

            if ( --flight.References == 0 )
            {
                _flights.Remove( key );
                flight.Dispose( );
                _changed.TrySetResult( );
                _changed = NewSignal( );
            }
        }
    }

    private static TaskCompletionSource NewSignal( ) => new( TaskCreationOptions.RunContinuationsAsynchronously );

    private sealed class Flight : IDisposable
    {
        public SemaphoreSlim Gate { get; } = new( 1, 1 );

        public int References { get; set; }

        public void Dispose( ) => Gate.Dispose( );
    }

    private sealed class Lease( VariantFlights owner, (long BlobId, string Digest) key, Flight flight ) : IDisposable
    {
        private VariantFlights? _owner = owner;

        public void Dispose( ) => Interlocked.Exchange( ref _owner, null )?.Release( key, flight, entered: true );
    }
}
