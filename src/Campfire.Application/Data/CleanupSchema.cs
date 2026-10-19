using Microsoft.Data.Sqlite;

namespace Campfire.Application.Data;

internal static class CleanupSchema
{
    private static readonly (string Name, string Sql)[] Objects =
    [
        ("campfire_room_deletions", "CREATE TABLE campfire_room_deletions (room_id INTEGER PRIMARY KEY NOT NULL, phase INTEGER NOT NULL, created_at datetime NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, next_attempt_at datetime NOT NULL)"),
        ("campfire_room_deletion_members", "CREATE TABLE campfire_room_deletion_members (room_id INTEGER NOT NULL, user_id INTEGER NOT NULL, PRIMARY KEY (room_id, user_id))"),
        ("campfire_cleanup", "CREATE TABLE campfire_cleanup (id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, kind INTEGER NOT NULL, record_id INTEGER, room_id INTEGER, blocked_by INTEGER, key varchar, service_name varchar, legacy INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0, created_at datetime NOT NULL, next_attempt_at datetime NOT NULL, CHECK (kind IN (0,1,2)), CHECK ((kind IN (0,1) AND record_id IS NOT NULL AND key IS NULL AND service_name IS NULL) OR (kind=2 AND record_id IS NOT NULL AND key IS NOT NULL AND service_name IS NOT NULL AND length(key) BETWEEN 1 AND 256)))"),
        ("campfire_cleanup_message", "CREATE UNIQUE INDEX campfire_cleanup_message ON campfire_cleanup (record_id) WHERE kind=0"),
        ("campfire_cleanup_file", "CREATE UNIQUE INDEX campfire_cleanup_file ON campfire_cleanup (service_name, key) WHERE kind=2"),
        ("campfire_cleanup_due", "CREATE INDEX campfire_cleanup_due ON campfire_cleanup (next_attempt_at, id)"),
        ("campfire_cleanup_blocked", "CREATE INDEX campfire_cleanup_blocked ON campfire_cleanup (blocked_by)"),
    ];

    public static List<string> Problems( SqliteConnection connection )
    {
        var problems = new List<string>( );
        foreach ( var (name, sql) in Objects )
        {
            using var command = connection.CreateCommand( "SELECT sql FROM sqlite_master WHERE name=@name" );
            command.Parameters.AddWithValue( "@name", name );
            if ( command.ExecuteScalar( ) is string actual && Canonical( actual ) != Canonical( sql ) )
            {
                problems.Add( $"cleanup object {name} differs (unsupported development cleanup schema)" );
            }
        }

        return problems;
    }

    public static void Prepare( SqliteConnection connection )
    {
        using var transaction = connection.BeginTransaction( );
        foreach ( var (name, sql) in Objects )
        {
            using var exists = connection.CreateCommand( "SELECT COUNT(*) FROM sqlite_master WHERE name=@name" );
            exists.Transaction = transaction;
            exists.Parameters.AddWithValue( "@name", name );
            if ( ( long ) exists.ExecuteScalar( )! == 0 )
            {
                using var command = connection.CreateCommand( sql );
                command.Transaction = transaction;
                command.ExecuteNonQuery( );
            }
        }

        transaction.Commit( );
    }

    public static void RemoveOwned( SchemaShape shape )
    {
        foreach ( var (name, _) in Objects )
        {
            shape.Tables.Remove( name );
            shape.Indexes.Remove( name );
        }

    }

    private static string Canonical( string sql ) =>
        string.Concat( sql.Where( character => !char.IsWhiteSpace( character ) && character is not ('"' or ';') ) ).ToLowerInvariant( );
}
