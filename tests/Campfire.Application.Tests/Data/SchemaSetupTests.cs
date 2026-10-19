using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

using Campfire.Application.Data;
using Campfire.Application.Tests.Support;

using Microsoft.Data.Sqlite;

namespace Campfire.Application.Tests.Data;

public sealed class SchemaSetupTests
{
    [Fact]
    public void A_rails_database_prepares_cleanup_without_changing_migration_metadata( )
    {
        using var connection = new SqliteConnection( "Data Source=:memory:" );
        connection.Open( );
        SchemaSetup.Load( connection, "test", TimeProvider.System );
        var metadata = Sql.Scalar( connection, "SELECT group_concat(key||'='||value) FROM ar_internal_metadata" );
        SchemaSetup.Prepare( connection, "test", TimeProvider.System );
        SchemaSetup.Prepare( connection, "test", TimeProvider.System );
        Assert.Equal( 3L, Sql.Scalar( connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('campfire_room_deletions','campfire_room_deletion_members','campfire_cleanup')" ) );
        Assert.Equal( metadata, Sql.Scalar( connection, "SELECT group_concat(key||'='||value) FROM ar_internal_metadata" ) );
        Assert.Equal( 15L, Sql.Scalar( connection, "SELECT COUNT(*) FROM schema_migrations" ) );
    }

    [Fact]
    public async Task A_changed_cleanup_table_is_rejected_before_boot_repairs_other_objects( )
    {
        using var test = await TestDatabase.CreateAsync( fixtures: false );
        await test.ExecuteAsync( "ALTER TABLE campfire_cleanup ADD COLUMN unexpected TEXT" );
        await test.ExecuteAsync( "DROP INDEX campfire_cleanup_due" );
        using var connection = test.OpenSideConnection( );
        var failure = Assert.Throws<SchemaMismatchException>( ( ) => SchemaSetup.Prepare( connection, "test", TimeProvider.System ) );
        Assert.Contains( "cleanup object campfire_cleanup differs", failure.Message, StringComparison.Ordinal );
        Assert.Equal( 0L, Sql.Scalar( connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='campfire_cleanup_due'" ) );
    }

    [Fact]
    public async Task A_missing_cleanup_index_is_restored_without_losing_pending_work( )
    {
        using var test = await TestDatabase.CreateAsync( fixtures: false );
        await test.ExecuteAsync( "INSERT INTO campfire_room_deletions (room_id,phase,created_at,next_attempt_at) VALUES (12,0,@p0,@p0)", test.Clock.Now );
        await test.ExecuteAsync( "DROP INDEX campfire_cleanup_due" );
        using var connection = test.OpenSideConnection( );
        SchemaSetup.Prepare( connection, "test", TimeProvider.System );
        Assert.Equal( 1L, Sql.Scalar( connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='campfire_cleanup_due'" ) );
        Assert.Equal( 1L, Sql.Scalar( connection, "SELECT COUNT(*) FROM campfire_room_deletions WHERE room_id=12" ) );
    }

    [Fact]
    public async Task An_empty_database_gets_schema_sql_with_its_migration_and_metadata_rows( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( fixtures: false );

        Assert.Equal( SchemaPreparation.Loaded, testDatabase.Database.Preparation );
        Assert.Equal( SchemaSetup.PinnedVersion, await testDatabase.ScalarAsync<string>( "SELECT version FROM schema_migrations ORDER BY rowid LIMIT 1" ) );
        Assert.Equal( "20231215043540", await testDatabase.ScalarAsync<string>( "SELECT version FROM schema_migrations ORDER BY rowid DESC LIMIT 1" ) );
        Assert.Equal( 15, await testDatabase.ScalarAsync<long>( "SELECT COUNT(*) FROM schema_migrations" ) );
        Assert.Equal( "test", await testDatabase.ScalarAsync<string>( "SELECT value FROM ar_internal_metadata WHERE key = 'environment'" ) );
        Assert.Equal( SchemaSetup.SchemaSha1, await testDatabase.ScalarAsync<string>( "SELECT value FROM ar_internal_metadata WHERE key = 'schema_sha1'" ) );
        Assert.Equal(
            "environment,schema_sha1",
            await testDatabase.ScalarAsync<string>( "SELECT group_concat(key) FROM (SELECT key FROM ar_internal_metadata ORDER BY rowid)" ) );
        Assert.True( RailsTimestamp.TryParse(
            await testDatabase.ScalarAsync<string>( "SELECT created_at FROM ar_internal_metadata WHERE key = 'environment'" ),
            out _ ) );
    }

    [Fact]
    public async Task The_search_index_uses_the_porter_tokenizer( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( fixtures: false );
        using var connection = testDatabase.OpenSideConnection( );

        Sql.Execute( connection, "INSERT INTO message_search_index(rowid, body) VALUES (1, 'running dogs')" );

        Assert.Equal( 1L, Sql.Scalar( connection, "SELECT rowid FROM message_search_index WHERE body MATCH 'run'" ) );
    }

    [Fact]
    public async Task The_pinned_schema_is_accepted_on_the_next_boot( )
    {
        var directory = await CreatePreparedDatabaseAsync( );

        using var database = Open( directory );

        Assert.Equal( SchemaPreparation.Accepted, database.Preparation );
    }

    [Fact]
    public async Task The_pinned_schema_with_an_unknown_later_migration_is_rejected( )
    {
        var directory = await CreatePreparedDatabaseAsync( );
        Alter( directory, "INSERT INTO schema_migrations (version) VALUES ('20260101000000')" );

        var error = Assert.Throws<SchemaMismatchException>( ( ) => Open( directory ) );

        Assert.Contains( "20260101000000", error.Message, StringComparison.Ordinal );
    }

    [Fact]
    public async Task An_unknown_extra_index_is_rejected( )
    {
        var directory = await CreatePreparedDatabaseAsync( );
        Alter( directory, "CREATE INDEX \"index_messages_on_client_message_id\" ON \"messages\" (\"client_message_id\")" );

        var error = Assert.Throws<SchemaMismatchException>( ( ) => Open( directory ) );

        Assert.Contains( "unexpected index index_messages_on_client_message_id", error.Message, StringComparison.Ordinal );
    }

    [Fact]
    public async Task The_rust_index_name_on_other_columns_is_rejected( )
    {
        var directory = await CreatePreparedDatabaseAsync( );
        Alter(
            directory,
            "DROP INDEX index_messages_on_room_id_and_created_at; CREATE INDEX \"index_messages_on_room_id_and_created_at\" ON \"messages\" (\"created_at\")" );

        Assert.Throws<SchemaMismatchException>( ( ) => Open( directory ) );
    }

    [Fact]
    public async Task A_missing_migration_or_changed_column_is_rejected( )
    {
        var missing = await CreatePreparedDatabaseAsync( );
        Alter( missing, $"DELETE FROM schema_migrations WHERE version = '{SchemaSetup.PinnedVersion}'" );
        var changed = await CreatePreparedDatabaseAsync( );
        Alter( changed, "ALTER TABLE users ADD COLUMN nickname varchar" );

        Assert.Contains( "migrations not run", Assert.Throws<SchemaMismatchException>( ( ) => Open( missing ) ).Message, StringComparison.Ordinal );
        Assert.Contains( "table users differs", Assert.Throws<SchemaMismatchException>( ( ) => Open( changed ) ).Message, StringComparison.Ordinal );
    }

    [Fact]
    public void A_database_with_other_tables_and_no_migrations_is_rejected( )
    {
        var directory = TestDatabase.NewDirectory( );
        Directory.CreateDirectory( Path.Combine( directory, "db" ) );
        Alter( directory, "CREATE TABLE notes (id integer PRIMARY KEY)" );

        Assert.Throws<SchemaMismatchException>( ( ) => Open( directory ) );
    }

    [Fact]
    public async Task A_database_rails_built_by_migrations_is_accepted_despite_column_order_and_ddl_spelling( )
    {
        var directory = await CreatePreparedDatabaseAsync( );

        // Migrations add columns at the end and spell the FTS table in lower case.
        Alter( directory,
            "DROP TABLE message_search_index;" +
            "create virtual table message_search_index using fts5(body, tokenize=porter);" +
            "CREATE TABLE \"accounts_migrated\" (\"id\" integer PRIMARY KEY AUTOINCREMENT NOT NULL, \"name\" varchar NOT NULL, \"created_at\" datetime(6) " +
                "NOT NULL, \"updated_at\" datetime(6) NOT NULL, \"join_code\" varchar NOT NULL, \"custom_styles\" text, \"settings\" json, " +
                "\"singleton_guard\" integer DEFAULT 0 NOT NULL);" +
            "DROP TABLE accounts; ALTER TABLE accounts_migrated RENAME TO accounts;" +
            "CREATE UNIQUE INDEX \"index_accounts_on_singleton_guard\" ON \"accounts\" (\"singleton_guard\");" );

        using var database = Open( directory );

        Assert.Equal( SchemaPreparation.Accepted, database.Preparation );
    }

    [Fact]
    [SuppressMessage( "Security", "CA5350", Justification = "Rails records the SHA-1 of db/schema.rb; this reproduces it, not a security use." )]
    public void The_recorded_schema_sha1_is_the_sha1_of_the_reference_schema_rb( )
    {
        var hash = SHA1.HashData( File.ReadAllBytes( RepositoryPaths.ReferencePath( "db", "schema.rb" ) ) );

        Assert.Equal( SchemaSetup.SchemaSha1, Convert.ToHexStringLower( hash ) );
    }

    [Fact]
    public void The_migration_versions_are_the_reference_migrations( )
    {
        var versions = Directory.EnumerateFiles( RepositoryPaths.ReferencePath( "db", "migrate" ) )
            .Select( file => Path.GetFileName( file ).Split( '_' )[0] )
            .Order( StringComparer.Ordinal )
            .ToList( );

        Assert.Equal( versions, SchemaSetup.MigrationVersions );
    }

    [Fact]
    public void Schema_sql_holds_the_imported_schema_unchanged( )
    {
        var imported = RepositoryPaths.ReadVectorText( "db", "schema.sql" );

        Assert.Contains( imported, SchemaSetup.SchemaSql, StringComparison.Ordinal );
        Assert.Equal( File.ReadAllText( RepositoryPaths.Combine( "schema.sql" ) ), SchemaSetup.SchemaSql );
    }

    [Fact]
    public async Task The_row_column_lists_read_every_column_of_every_table_and_nothing_else( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( fixtures: false );
        using var connection = testDatabase.OpenSideConnection( );
        var schema = TableColumns( connection );

        // No query reads whole rows of these: SchemaSetup's own statements cover the framework tables, and variant
        // records are looked up for their id alone.
        string[] frameworkTables = ["active_storage_variant_records", "ar_internal_metadata", "schema_migrations", "campfire_cleanup", "campfire_room_deletions", "campfire_room_deletion_members"];
        var read = Columns.ByTable.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Split( ", " ).Select( column => Unqualified( entry.Key, column ) ).Order( StringComparer.Ordinal ).ToList( ) );

        Assert.Equal( schema.Keys.Except( frameworkTables ).Order( StringComparer.Ordinal ), read.Keys.Order( StringComparer.Ordinal ) );
        foreach ( var (table, columns) in read )
        {
            Assert.Equal( schema[table], columns );
        }
    }

    /// <summary>A column of a list, which must be qualified with its own table: <c>users.name</c> or <c>t."key"</c>.</summary>
    private static string Unqualified( string table, string column )
    {
        Assert.StartsWith( table + ".", column, StringComparison.Ordinal );
        return column[(table.Length + 1)..].Trim( '"' );
    }

    private static async Task<string> CreatePreparedDatabaseAsync( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( fixtures: false );
        var directory = TestDatabase.NewDirectory( );
        Directory.CreateDirectory( Path.Combine( directory, "db" ) );

        // A consistent copy, including what is still in the WAL.
        using var connection = testDatabase.OpenSideConnection( );
        using var command = connection.CreateCommand( );
        command.CommandText = "VACUUM INTO @path";
        command.Parameters.AddWithValue( "@path", Path.Combine( directory, "db", "test.sqlite3" ) );
        command.ExecuteNonQuery( );
        return directory;
    }

    private static CampfireDatabase Open( string directory ) =>
        CampfireDatabase.Open(
            new DatabaseOptions { StorageRoot = directory, Environment = "test", ReaderCount = 1 },
            new TestClock( ),
            new RecordingHooks( ).ToHooks( new BasicBodyText( ) ) );

    private static void Alter( string directory, string sql )
    {
        using var connection = new SqliteConnection( $"Data Source={Path.Combine( directory, "db", "test.sqlite3" )};Pooling=False" );
        connection.Open( );
        Sql.Execute( connection, sql );
    }

    private static Dictionary<string, List<string>> TableColumns( SqliteConnection connection )
    {
        var tables = new List<string>( );
        using ( var command = connection.CreateCommand( ) )
        {
            command.CommandText = "SELECT name FROM pragma_table_list WHERE schema = 'main' AND type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\'";
            using var reader = command.ExecuteReader( );
            while ( reader.Read( ) )
            {
                tables.Add( reader.GetString( 0 ) );
            }
        }

        return tables.ToDictionary( table => table, table =>
        {
            using var command = connection.CreateCommand( );
            command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY name";
            using var reader = command.ExecuteReader( );
            var columns = new List<string>( );
            while ( reader.Read( ) )
            {
                columns.Add( reader.GetString( 0 ) );
            }

            return columns.Order( StringComparer.Ordinal ).ToList( );
        } );
    }
}
