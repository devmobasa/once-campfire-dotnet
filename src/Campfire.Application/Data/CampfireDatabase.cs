using System.Collections.Concurrent;

using Campfire.Application.Data.Hooks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// CampfireDatabase has a Path property of its own.
using FilePath = System.IO.Path;

namespace Campfire.Application.Data;

/// <summary>
/// Owns the SQLite connections: one writer behind a <c>SemaphoreSlim(1)</c> lease and N readers behind a
/// <c>SemaphoreSlim(N)</c> lease, plus a checkpointer that keeps WAL checkpoints off the writer
/// (<see cref="WalCheckpointer"/>). Callers await a lease, then run synchronously with a <see cref="CampfireDb"/>
/// session on the leased connection; Microsoft.Data.Sqlite has no asynchronous I/O, so this bounds the threads SQLite
/// can block.
/// </summary>
/// <remarks>
/// Cancellation applies only while waiting for a lease. Once work starts it runs to the end, so a disconnected
/// caller can neither interrupt a transaction nor skip after-commit work.
/// </remarks>
public sealed class CampfireDatabase : IDisposable
{
    private readonly SqliteConnection _writer;

    private readonly SemaphoreSlim _writerGate;

    private readonly SemaphoreSlim _readerGate;

    private readonly ConcurrentStack<SqliteConnection> _idleReaders = new( );

    private readonly List<SqliteConnection> _readers;

    private readonly WalCheckpointer _checkpointer;

    private bool _disposed;

    private CampfireDatabase(
        string path,
        SqliteConnection writer,
        List<SqliteConnection> readers,
        SemaphoreSlim writerGate,
        WalCheckpointer checkpointer,
        TimeProvider clock,
        CampfireHooks hooks )
    {
        Path = path;
        Clock = clock;
        Hooks = hooks;
        _writer = writer;
        _readers = readers;
        _writerGate = writerGate;
        _checkpointer = checkpointer;
        _readerGate = new SemaphoreSlim( readers.Count, readers.Count );
        foreach ( var connection in readers )
        {
            _idleReaders.Push( connection );
        }
    }

    private bool HasUpdatedMessageIndex { get; init; }

    /// <summary>The database file.</summary>
    public string Path { get; }

    public TimeProvider Clock { get; }

    public CampfireHooks Hooks { get; }

    /// <summary>What preparing the schema did when this instance opened the database.</summary>
    public SchemaPreparation Preparation { get; private init; }

    /// <summary>
    /// Opens (creating if needed) <c>&lt;storage&gt;/db/&lt;environment&gt;.sqlite3</c>, loads or checks the schema
    /// (<see cref="SchemaSetup"/>), and opens the reader connections.
    /// </summary>
    /// <param name="options">Where the database lives and how many readers serve it.</param>
    /// <param name="clock">The clock for timestamps.</param>
    /// <param name="hooks">The side effects that leave the database.</param>
    /// <param name="logger">Startup index creation and background checkpoint diagnostics; nowhere when omitted.</param>
    /// <exception cref="SchemaMismatchException">The existing database is not one this build can run on.</exception>
    public static CampfireDatabase Open( DatabaseOptions options, TimeProvider clock, CampfireHooks hooks, ILogger? logger = null )
    {
        ArgumentNullException.ThrowIfNull( options );

        options.Validate( );
        var path = FilePath.GetFullPath( options.DatabasePath );
        Directory.CreateDirectory( FilePath.GetDirectoryName( path )! );

        var writer = SqliteConnections.Open( path, reader: false );
        var readers = new List<SqliteConnection>( );
        var writerGate = new SemaphoreSlim( 1, 1 );
        WalCheckpointer? checkpointer = null;
        try
        {
            WalConnections.DisableAutoCheckpoint( writer );
            var preparation = SchemaSetup.Prepare( writer, options.Environment, clock, options.CreateMessageIndex, logger, options.CreateUpdatedMessageIndex );
            using var indexCommand = writer.CreateCommand( "SELECT 1 FROM sqlite_master WHERE type='index' AND name=@name AND tbl_name='messages'" );
            indexCommand.Parameters.AddWithValue( "@name", SchemaSetup.UpdatedMessagesIndex );
            var hasUpdatedMessageIndex = indexCommand.ExecuteScalar( ) is not null;
            for ( var index = 0; index < options.ReaderCount; index++ )
            {
                readers.Add( SqliteConnections.Open( path, reader: true ) );
            }

            WalConnections.TakeAppendedPages( writer );
            checkpointer = new WalCheckpointer( path, writerGate, options.Checkpoints, logger ?? NullLogger.Instance );
            return new CampfireDatabase( path, writer, readers, writerGate, checkpointer, clock, hooks ) { Preparation = preparation, HasUpdatedMessageIndex = hasUpdatedMessageIndex };
        }
        catch
        {
            checkpointer?.Dispose( );
            readers.ForEach( reader => reader.Dispose( ) );
            writer.Dispose( );
            writerGate.Dispose( );
            throw;
        }
    }

    /// <summary>
    /// Waits for the writer connection, and, while the WAL is at its limit, for the checkpointer's <c>TRUNCATE</c>.
    /// Dispose the lease to release it.
    /// </summary>
    public async Task<WriterLease> AcquireWriterAsync( CancellationToken cancellationToken )
    {
        while ( true )
        {
            ObjectDisposedException.ThrowIf( _disposed, this );
            await _writerGate.WaitAsync( cancellationToken );
            if ( _checkpointer.WalLimitWait is not { } truncation )
            {
                return new WriterLease( this, _writer, new CampfireDb( _writer, HasUpdatedMessageIndex ) );
            }

            // Hand the lease to the checkpointer for its TRUNCATE, then queue again.
            _writerGate.Release( );
            await truncation.WaitAsync( cancellationToken );
        }
    }

    /// <summary>Waits for a reader connection. Dispose the lease to return it.</summary>
    public async Task<ReaderLease> AcquireReaderAsync( CancellationToken cancellationToken )
    {
        ObjectDisposedException.ThrowIf( _disposed, this );
        await _readerGate.WaitAsync( cancellationToken );
        if ( !_idleReaders.TryPop( out var reader ) )
        {
            _readerGate.Release( );
            throw new InvalidOperationException( "The reader pool and its semaphore disagree" );
        }

        return new ReaderLease( this, reader, new CampfireDb( reader, HasUpdatedMessageIndex ) );
    }

    /// <summary>Runs <paramref name="work"/> as one write transaction, with its after-commit work, under the writer lease.</summary>
    public async Task<T> WriteAsync<T>( Func<WriteScope, T> work, CancellationToken cancellationToken )
    {
        using var lease = await AcquireWriterAsync( cancellationToken );
        return lease.Transaction( work );
    }

    /// <inheritdoc cref="WriteAsync{T}(Func{WriteScope, T}, CancellationToken)"/>
    public async Task WriteAsync( Action<WriteScope> work, CancellationToken cancellationToken )
    {
        using var lease = await AcquireWriterAsync( cancellationToken );
        lease.Transaction( work );
    }

    /// <summary>Runs <paramref name="read"/> on a reader connection.</summary>
    public async Task<T> ReadAsync<T>( Func<CampfireDb, T> read, CancellationToken cancellationToken )
    {
        using var lease = await AcquireReaderAsync( cancellationToken );
        return read( lease.Db );
    }

    /// <summary>
    /// Copies every committed write into the database file and empties the WAL (<c>wal_checkpoint(TRUNCATE)</c>),
    /// holding the writer lease meanwhile, as before a backup. Returns whether it completed; a reader still on an older
    /// snapshot past the 5 s busy timeout keeps it from finishing. Never call it while holding a lease.
    /// </summary>
    public Task<bool> CheckpointAsync( CancellationToken cancellationToken )
    {
        ObjectDisposedException.ThrowIf( _disposed, this );
        return _checkpointer.TruncateAsync( cancellationToken );
    }

    /// <summary>The background checkpointer, for tests.</summary>
    internal WalCheckpointer Checkpointer => _checkpointer;

    /// <summary>
    /// Stops the checkpointer after a final checkpoint (<see cref="WalCheckpointer.Dispose"/>), then closes every
    /// connection. Leases must have been released.
    /// </summary>
    public void Dispose( )
    {
        if ( _disposed )
        {
            return;
        }

        _disposed = true;
        _checkpointer.Dispose( );
        _readers.ForEach( reader => reader.Dispose( ) );
        _writer.Dispose( );
        _writerGate.Dispose( );
        _readerGate.Dispose( );
    }

    /// <summary>Reports what the writer appended to the WAL to the checkpointer. Called under the writer lease.</summary>
    internal void NoteWrites( ) => _checkpointer.NoteAppendedPages( WalConnections.TakeAppendedPages( _writer ) );

    internal void ReleaseWriter( ) => _writerGate.Release( );

    internal void ReturnReader( SqliteConnection reader )
    {
        _idleReaders.Push( reader );
        _readerGate.Release( );
    }
}
