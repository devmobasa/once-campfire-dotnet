using Campfire.Application.Data;
using Campfire.Application.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Campfire.Application.Tests.Data;

public sealed class MessageIndexLoggingTests
{
    [Theory]
    [InlineData( false, true, false )]
    [InlineData( true, true, false )]
    [InlineData( false, false, false )]
    [InlineData( true, false, false )]
    [InlineData( false, true, true )]
    [InlineData( true, true, true )]
    [InlineData( false, false, true )]
    [InlineData( true, false, true )]
    public void Boot_logs_before_index_creation_and_after_completion_only_when_creation_is_needed( bool existing, bool enabled, bool updated )
    {
        var directory = TestDatabase.NewDirectory( );
        var path = Path.Combine( directory, "db", "test.sqlite3" );
        try
        {
            if ( existing )
            {
                Open( directory, false );
            }

            var index = updated ? SchemaSetup.UpdatedMessagesIndex : SchemaSetup.MessagesIndex;
            var logger = new IndexLogger( path, index );
            Open( directory, enabled, logger, updated );
            if ( enabled )
            {
                Assert.Equal( new[] { (101, 0L), (102, 1L) }, logger.Events.Select( entry => (entry.Id, entry.Indexes) ) );
                Assert.All( logger.Events, entry => Assert.Contains( index, entry.Message, StringComparison.Ordinal ) );
                Assert.Contains( "startup waits", logger.Events[0].Message, StringComparison.Ordinal );
            }
            else
            {
                Assert.Empty( logger.Events );
            }

            logger.Events.Clear( );
            Open( directory, enabled, logger, updated );
            Assert.Empty( logger.Events ); // Reopening with an existing index or opt-out does no index work.
        }
        finally
        {
            Directory.Delete( directory, recursive: true );
        }
    }

    private static void Open( string directory, bool enabled, ILogger? logger = null, bool updated = false )
    {
        using var database = CampfireDatabase.Open(
            new DatabaseOptions { StorageRoot = directory, Environment = "test", ReaderCount = 1, CreateMessageIndex = !updated && enabled, CreateUpdatedMessageIndex = updated && enabled },
            new TestClock( ), new RecordingHooks( ).ToHooks( new BasicBodyText( ) ), logger );
    }

    private sealed class IndexLogger( string path, string index ) : ILogger
    {
        internal List<(int Id, long Indexes, string Message)> Events { get; } = [];
        public IDisposable? BeginScope<TState>( TState state ) where TState : notnull => null;
        public bool IsEnabled( LogLevel logLevel ) => true;
        public void Log<TState>( LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter )
        {
            if ( level != LogLevel.Information )
            {
                return;
            }

            using var connection = new SqliteConnection( $"Data Source={path};Mode=ReadOnly;Pooling=False" );
            connection.Open( );
            using var command = connection.CreateCommand( );
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@index";
            command.Parameters.AddWithValue( "@index", index );
            Events.Add( (id.Id, ( long ) command.ExecuteScalar( )!, formatter( state, exception )) );
        }
    }
}
