using System.Globalization;
using Campfire.Application.Data;
using Campfire.Application.Messages;
using Campfire.Application.Tests.Support;

using Microsoft.Data.Sqlite;

namespace Campfire.Application.Tests.Messages;

/// <summary><c>Message::Pagination</c>: pages of 40 by <c>created_at</c> alone, bounded in SQL.</summary>
public sealed class MessagePagesTests( ITestOutputHelper output )
{
    private static readonly long Watercooler = TestDatabase.Id( "watercooler" );

    [Fact]
    public async Task Pages_before_after_and_around_a_message( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var all = await testDatabase.ReadAsync( db => MessagePages.LastPage( db, Watercooler ) );
        var middle = all[5];

        Assert.Equal( 10, all.Count );
        Assert.True( all.Zip( all.Skip( 1 ) ).All( pair => pair.First.CreatedAt <= pair.Second.CreatedAt ) );
        Assert.False( await testDatabase.ReadAsync( db => MessagePages.IsPaged( db, Watercooler ) ) );
        Assert.Equal( Ids( all[..5] ), Ids( await testDatabase.ReadAsync( db => MessagePages.PageBefore( db, Watercooler, middle ) ) ) );
        Assert.Equal( Ids( all[6..] ), Ids( await testDatabase.ReadAsync( db => MessagePages.PageAfter( db, Watercooler, middle ) ) ) );
        Assert.Equal( Ids( all ), Ids( await testDatabase.ReadAsync( db => MessagePages.PageAround( db, Watercooler, middle ) ) ) );
        Assert.True( await testDatabase.ReadAsync( db => MessagePages.ExistsBefore( db, Watercooler, middle ) ) );
        Assert.False( await testDatabase.ReadAsync( db => MessagePages.ExistsAfter( db, Watercooler, all[9] ) ) );
    }

    [Fact]
    public async Task Pages_hold_forty_and_page_around_at_most_eighty_one( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var fixtures = await testDatabase.ReadAsync( db => MessagePages.LastPage( db, Watercooler ) );
        for ( var index = 0; index < 80; index++ )
        {
            testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );
            await testDatabase.PostMessageAsync( "watercooler", "david", $"message {index}" );
        }

        var last = await testDatabase.ReadAsync( db => MessagePages.LastPage( db, Watercooler ) );
        var first = await testDatabase.ReadAsync( db => MessagePages.FirstPage( db, Watercooler ) );
        var middle = (await testDatabase.ReadAsync( db => MessagePages.PageAfter( db, Watercooler, fixtures[^1] ) ))[39];
        var around = await testDatabase.ReadAsync( db => MessagePages.PageAround( db, Watercooler, middle ) );

        Assert.True( await testDatabase.ReadAsync( db => MessagePages.IsPaged( db, Watercooler ) ) );
        Assert.Equal( MessagePages.PageSize, last.Count );
        Assert.Equal( fixtures[0].Id, first[0].Id );
        Assert.Equal( 81, around.Count );
        Assert.Equal( middle.Id, around[40].Id );
    }

    [Fact]
    public async Task Created_and_updated_since_pages( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var fixtures = await testDatabase.ReadAsync( db => MessagePages.LastPage( db, Watercooler ) );
        for ( var index = 0; index < MessagePages.PageSize; index++ )
        {
            testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );
            await testDatabase.PostMessageAsync( "watercooler", "david", $"message {index}" );
        }

        var since = fixtures[^1].CreatedAt;
        var created = await testDatabase.ReadAsync( db => MessagePages.PageCreatedSince( db, Watercooler, since ) );
        var updated = await testDatabase.ReadAsync( db => MessagePages.PageUpdatedSince( db, Watercooler, since, Ids( created ) ) );

        Assert.Equal( MessagePages.PageSize, created.Count );
        Assert.DoesNotContain( updated, message => created.Any( createdMessage => createdMessage.Id == message.Id ) );
        Assert.Contains( updated, message => message.Id == TestDatabase.Id( "thirteenth" ) );
    }

    [Fact]
    public async Task Ties_on_created_at_come_back_exactly_as_the_rails_statement_returns_them( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        testDatabase.Clock.TravelTo( new DateTime( 2026, 9, 26, 12, 0, 0, DateTimeKind.Utc ) );
        for ( var index = 0; index < 45; index++ )
        {
            await testDatabase.PostMessageAsync( "watercooler", "david", $"tied {index}" );
        }

        var last = await testDatabase.ReadAsync( db => MessagePages.LastPage( db, Watercooler ) );
        var first = await testDatabase.ReadAsync( db => MessagePages.FirstPage( db, Watercooler ) );

        using var side = testDatabase.OpenSideConnection( );
        var railsLast = RoomIds(
            side,
            "SELECT \"messages\".\"id\" FROM \"messages\" WHERE \"messages\".\"room_id\" = @room ORDER BY \"messages\".\"created_at\" DESC LIMIT 40" );
        railsLast.Reverse( );
        var railsFirst = RoomIds(
            side,
            "SELECT \"messages\".\"id\" FROM \"messages\" WHERE \"messages\".\"room_id\" = @room ORDER BY \"messages\".\"created_at\" ASC LIMIT 40" );
        Assert.Equal( railsLast, Ids( last ) );
        Assert.Equal( railsFirst, Ids( first ) );
    }

    [Fact]
    public async Task Each_page_is_one_statement_bounded_in_sql( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        for ( var index = 0; index < 60; index++ )
        {
            testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );
            await testDatabase.PostMessageAsync( "watercooler", "david", $"message {index}" );
        }

        var middle = (await testDatabase.ReadAsync( db => MessagePages.FirstPage( db, Watercooler ) ))[30];
        using var reader = await testDatabase.Database.AcquireReaderAsync( TestDatabase.Cancellation );
        using var log = new StatementLog( reader.Db.Connection );
        var db = reader.Db;
        (string Page, int Statements, Func<List<Message>> Read)[] pages =
        [
            ("last_page", 1, ( ) => MessagePages.LastPage( db, Watercooler )),
            ("first_page", 1, ( ) => MessagePages.FirstPage( db, Watercooler )),
            ("page_before", 1, ( ) => MessagePages.PageBefore( db, Watercooler, middle )),
            ("page_after", 1, ( ) => MessagePages.PageAfter( db, Watercooler, middle )),
            ("page_around", 2, ( ) => MessagePages.PageAround( db, Watercooler, middle )),
            ("page_created_since", 1, ( ) => MessagePages.PageCreatedSince( db, Watercooler, middle.CreatedAt )),
            ("page_updated_since", 1, ( ) => MessagePages.PageUpdatedSince( db, Watercooler, middle.CreatedAt, [middle.Id] )),
        ];

        foreach ( var (page, statements, read) in pages )
        {
            log.Clear( );
            var messages = read( );

            // One statement per bound, each stopping at a page in SQLite: the room is never read whole.
            Assert.True( messages.Count <= (2 * MessagePages.PageSize) + 1, page );
            Assert.Equal( statements, log.Statements.Count );
            Assert.All( log.Statements, sql =>
            {
                Assert.Contains( "WHERE messages.room_id = ", sql, StringComparison.Ordinal );
                Assert.EndsWith( $"LIMIT {MessagePages.PageSize}", sql, StringComparison.Ordinal );
            } );
        }
    }

    [Fact]
    public async Task Page_statements_search_the_room_index_instead_of_scanning_messages( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        using var side = testDatabase.OpenSideConnection( );

        string[] queries =
        [
            $"{MessagePages.InRoom} {MessagePages.NewestFirst}",
            $"{MessagePages.InRoom} AND messages.created_at > @time {MessagePages.OldestFirst}",
        ];
        foreach ( var sql in queries )
        {
            using var command = side.CreateCommand( );
            command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
            command.Parameters.AddWithValue( "@roomId", Watercooler );
            command.Parameters.AddWithValue( "@limit", MessagePages.PageSize );
            command.Parameters.AddWithValue( "@time", "2026-01-01 00:00:00" );
            using var plan = command.ExecuteReader( );
            var steps = new List<string>( );
            while ( plan.Read( ) )
            {
                steps.Add( plan.GetString( 3 ) );
            }

            Assert.Contains( steps, step => step.StartsWith( "SEARCH messages USING INDEX index_messages_on_room_id", StringComparison.Ordinal ) );
            Assert.DoesNotContain( steps, step => step.StartsWith( "SCAN messages", StringComparison.Ordinal ) );
        }
    }

    private static List<long> Ids( IEnumerable<Message> messages ) => [.. messages.Select( message => message.Id )];

    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public async Task Updated_since_returns_the_newest_created_changes_after_room_time_and_id_filters( bool updatedIndex )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var since = new DateTime( 2100, 1, 1, 0, 0, 0, DateTimeKind.Utc );
        for ( var index = 0; index < 72; index++ )
        {
            var created = new DateTime( 2020, 1, 1, 0, 0, 0, DateTimeKind.Utc ).AddMinutes( index );
            var updated = index == 70 ? since : since.AddMinutes( 100 - index );
            await testDatabase.ExecuteAsync(
                "INSERT INTO messages (id, client_message_id, creator_id, room_id, created_at, updated_at) VALUES (@p0, @p1, @p2, @p3, @p4, @p5)",
                2_000_000_000L + index, $"changed-{index}", TestDatabase.Id( "david" ), index == 71 ? TestDatabase.Id( "designers" ) : Watercooler,
                created.ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture ), updated.ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture ) );
        }

        testDatabase.Database.Dispose( );
        using var reopened = CampfireDatabase.Open(
            new DatabaseOptions { StorageRoot = testDatabase.Directory, Environment = "test", ReaderCount = 1, CreateUpdatedMessageIndex = updatedIndex },
            testDatabase.Clock, testDatabase.Hooks.ToHooks( testDatabase.BodyText ) );
        var excluded = await reopened.ReadAsync( db => MessagePages.PageUpdatedSince( db, Watercooler, since, [2_000_000_067L, 2_000_000_068L, 2_000_000_069L] ), TestDatabase.Cancellation );
        var complete = await reopened.ReadAsync( db => MessagePages.PageUpdatedSince( db, Watercooler, since, [] ), TestDatabase.Cancellation );

        Assert.Equal( Enumerable.Range( 27, 40 ).Select( index => 2_000_000_000L + index ), Ids( excluded ) );
        Assert.Equal( Enumerable.Range( 30, 40 ).Select( index => 2_000_000_000L + index ), Ids( complete ) );
    }

    [Theory]
    [InlineData( false, false )]
    [InlineData( true, false )]
    [InlineData( true, true )]
    [InlineData( false, true )]
    public async Task Updated_since_uses_the_available_index_without_sacrificing_ordered_early_exit( bool creationIndex, bool updateIndex )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await testDatabase.ExecuteAsync(
            """
            WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 4000)
            INSERT INTO messages (client_message_id, creator_id, room_id, created_at, updated_at)
            SELECT 'old-' || value, @p0, @p1, '2020-01-01 00:00:00', '2020-01-01 00:00:00' FROM numbers
            """, TestDatabase.Id( "david" ), Watercooler );
        if ( !creationIndex )
        {
            await testDatabase.ExecuteAsync( "DROP INDEX index_messages_on_room_id_and_created_at" );
        }

        if ( updateIndex )
        {
            await testDatabase.ExecuteAsync( "CREATE INDEX index_messages_on_room_id_and_updated_at ON messages (room_id, updated_at)" );
        }

        await testDatabase.ExecuteAsync( "ANALYZE" );
        testDatabase.Database.Dispose( );
        using var reopened = CampfireDatabase.Open(
            new DatabaseOptions { StorageRoot = testDatabase.Directory, Environment = "test", ReaderCount = 1, CreateMessageIndex = creationIndex, CreateUpdatedMessageIndex = false },
            testDatabase.Clock, testDatabase.Hooks.ToHooks( testDatabase.BodyText ) );
        using var reader = await reopened.AcquireReaderAsync( TestDatabase.Cancellation );
        string sql;
        using ( var log = new StatementLog( reader.Db.Connection ) )
        {
            MessagePages.PageUpdatedSince( reader.Db, Watercooler, new DateTime( 2026, 9, 26, 12, 0, 0, DateTimeKind.Utc ), [] );
            sql = Assert.Single( log.Statements );
        }

        using var command = reader.Db.Connection.CreateCommand( );
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        using var plan = command.ExecuteReader( );
        var steps = new List<string>( );
        while ( plan.Read( ) )
        {
            steps.Add( plan.GetString( 3 ) );
        }

        output.WriteLine( $"creationIndex={creationIndex}, updateIndex={updateIndex}\n{sql}\n{string.Join( '\n', steps )}" );
        if ( updateIndex )
        {
            Assert.True( steps.Any( step => step.Contains( "index_messages_on_room_id_and_updated_at (room_id=? AND updated_at>?)", StringComparison.Ordinal ) ), string.Join( '\n', steps ) );
        }
        else
        {
            Assert.Contains( steps, step => step.StartsWith( "SEARCH messages ", StringComparison.Ordinal )
                && step.Contains( "(room_id=?)", StringComparison.Ordinal ) );
        }

        Assert.DoesNotContain( steps, step => step.StartsWith( "SCAN messages", StringComparison.Ordinal ) );
        if ( creationIndex && !updateIndex )
        {
            Assert.DoesNotContain( steps, step => step.Contains( "USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal ) );
            Assert.Contains( steps, step => step.Contains( "index_messages_on_room_id_and_created_at", StringComparison.Ordinal ) );
        }
        else
        {
            Assert.Contains( steps, step => step.Contains( "USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal ) );
        }
    }

    private static List<long> RoomIds( SqliteConnection connection, string sql )
    {
        using var command = connection.CreateCommand( );
        command.CommandText = sql;
        command.Parameters.AddWithValue( "@room", Watercooler );
        using var reader = command.ExecuteReader( );
        var ids = new List<long>( );
        while ( reader.Read( ) )
        {
            ids.Add( reader.GetInt64( 0 ) );
        }

        return ids;
    }
}
