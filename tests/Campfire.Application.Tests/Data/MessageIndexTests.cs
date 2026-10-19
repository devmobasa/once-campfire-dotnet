using Campfire.Application.Data;
using Campfire.Application.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Campfire.Application.Tests.Data;

public sealed class MessageIndexTests
{
    [Theory]
    [InlineData( true, false )]
    [InlineData( false, false )]
    [InlineData( true, true )]
    [InlineData( false, true )]
    public void Empty_and_existing_rails_databases_add_each_index_only_when_enabled( bool enabled, bool updated )
    {
        var index = updated ? SchemaSetup.UpdatedMessagesIndex : SchemaSetup.MessagesIndex;
        using var connection = new SqliteConnection( "Data Source=:memory:" );
        connection.Open( );
        Assert.Equal( SchemaPreparation.Loaded, SchemaSetup.Prepare( connection, "test", new TestClock( ), createMessageIndex: updated || enabled, createUpdatedMessageIndex: updated && enabled ) );
        Assert.Equal( enabled ? 1L : 0L, Count( connection, index ) );
        if ( enabled )
        {
            using var drop = connection.CreateCommand( );
            drop.CommandText = $"DROP INDEX {index}";
            drop.ExecuteNonQuery( );
        }

        Assert.Equal( SchemaPreparation.Accepted, SchemaSetup.Prepare( connection, "test", new TestClock( ), createMessageIndex: updated || enabled, createUpdatedMessageIndex: updated && enabled ) );
        Assert.Equal( enabled ? 1L : 0L, Count( connection, index ) );
        Assert.Equal( SchemaPreparation.Accepted, SchemaSetup.Prepare( connection, "test", new TestClock( ), createMessageIndex: updated || enabled, createUpdatedMessageIndex: updated && enabled ) );
        Assert.Equal( enabled ? 1L : 0L, Count( connection, index ) );
        Assert.Equal( SchemaPreparation.Accepted, SchemaSetup.Prepare( connection, "test", new TestClock( ), createMessageIndex: false ) );
        Assert.Equal( enabled ? 1L : 0L, Count( connection, index ) ); // Opt-out never removes a deployed index.
    }

    private static long Count( SqliteConnection connection, string index )
    {
        using var command = connection.CreateCommand( );
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@index";
        command.Parameters.AddWithValue( "@index", index );
        return ( long ) command.ExecuteScalar( )!;
    }

    [Theory]
    [InlineData( "room_id, updated_at", true, false, false )]
    [InlineData( "updated_at, room_id", false, false, false )]
    [InlineData( "room_id, created_at", false, false, false )]
    [InlineData( "room_id, updated_at", false, true, false )]
    [InlineData( "room_id, updated_at", false, false, true )]
    public void An_existing_optional_update_index_is_accepted_only_with_its_exact_shape( string columns, bool valid, bool unique, bool partial )
    {
        using var connection = new SqliteConnection( "Data Source=:memory:" );
        connection.Open( );
        SchemaSetup.Load( connection, "test", new TestClock( ) );
        using var command = connection.CreateCommand( );
        var modifier = unique ? "UNIQUE " : "";
        var predicate = partial ? " WHERE room_id > 0" : "";
        command.CommandText = $"CREATE {modifier}INDEX index_messages_on_room_id_and_updated_at ON messages ({columns}){predicate}";
        command.ExecuteNonQuery( );

        if ( valid )
        {
            SchemaSetup.Check( connection );
        }
        else
        {
            Assert.Throws<SchemaMismatchException>( ( ) => SchemaSetup.Check( connection ) );
            Assert.Throws<SchemaMismatchException>( ( ) => SchemaSetup.Prepare( connection, "test", new TestClock( ), createUpdatedMessageIndex: true ) );
        }
    }
}
