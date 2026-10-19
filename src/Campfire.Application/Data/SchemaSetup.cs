using System.Globalization;
using System.Reflection;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.Application.Data;

/// <summary>
/// Boot-time schema handling. An empty database gets <c>schema.sql</c>. An existing one is
/// accepted only when it is at the pinned migration with nothing later, and its tables and indexes match
/// <c>schema.sql</c>, allowing optional message indexes and exactly validated cleanup tables and indexes.
/// Boot adds missing cleanup objects without changing the Rails migration version. It does not use <c>EnsureCreated</c>.
/// </summary>
public static partial class SchemaSetup
{
    /// <summary>The last migration in Rails' <c>db/migrate</c> at the pin, and the version of <c>db/schema.rb</c>.</summary>
    public const string PinnedVersion = "20251212154340";

    /// <summary>SHA-1 of the pinned <c>db/schema.rb</c>, which <c>db:schema:load</c> records in <c>ar_internal_metadata</c>.</summary>
    public const string SchemaSha1 = "f75da8dad38bfb179ffd757bd7a7c2b3f818bc29";

    /// <summary>The approved optional message-page index, using Rails' conventional name.</summary>
    public const string MessagesIndex = "index_messages_on_room_id_and_created_at";

    /// <summary>The optional reconnect index. Creation is disabled by default.</summary>
    public const string UpdatedMessagesIndex = "index_messages_on_room_id_and_updated_at";

    /// <summary>The optional index's statement, exactly as <c>schema.sql</c> spells it.</summary>
    private const string MessagesIndexSql =
        $"CREATE INDEX IF NOT EXISTS \"{MessagesIndex}\" ON \"{TableNames.Messages}\" (\"room_id\", \"created_at\");";

    private const string UpdatedMessagesIndexSql =
        $"CREATE INDEX IF NOT EXISTS \"{UpdatedMessagesIndex}\" ON \"{TableNames.Messages}\" (\"room_id\", \"updated_at\");";

    private const string ResourceName = "Campfire.Application.Data.schema.sql";

    private static readonly Lazy<string> Script = new( LoadScript );

    private static readonly Lazy<SchemaShape> Expected = new( ReadExpectedShape );

    /// <summary>Every migration in Rails' <c>db/migrate</c> at the pin, oldest first.</summary>
    public static IReadOnlyList<string> MigrationVersions { get; } =
    [
        "20231215043540", "20231220143106", "20240110071740", "20240115124901", "20240130003150",
        "20240130213001", "20240131105830", "20240209110503", "20250825100957", "20250825100958",
        "20250825100959", "20251126092013", "20251126115722", "20251126130131", PinnedVersion,
    ];

    /// <summary>The embedded <c>schema.sql</c>.</summary>
    public static string SchemaSql => Script.Value;

    /// <summary>
    /// Loads the schema into an empty database, or checks an existing one.
    /// </summary>
    /// <param name="connection">An open writer connection.</param>
    /// <param name="environment">The Rails environment recorded in <c>ar_internal_metadata</c>.</param>
    /// <param name="clock">The clock for the metadata rows' timestamps.</param>
    /// <param name="createMessageIndex">Whether to create the optional <see cref="MessagesIndex"/>.</param>
    /// <param name="logger">The index creation's progress; nowhere when omitted.</param>
    /// <param name="createUpdatedMessageIndex">Whether to create the optional reconnect index.</param>
    /// <exception cref="SchemaMismatchException">The database is not one this build can run on.</exception>
    public static SchemaPreparation Prepare(
        SqliteConnection connection, string environment, TimeProvider clock, bool createMessageIndex = true, ILogger? logger = null, bool createUpdatedMessageIndex = false )
    {
        logger ??= NullLogger.Instance;
        var empty = IsEmpty( connection );
        if ( empty )
        {
            if ( createMessageIndex )
            {
                CreatingMessageIndex( logger, MessagesIndex );
            }

            Load( connection, environment, clock, createMessageIndex );
            if ( createMessageIndex )
            {
                CreatedMessageIndex( logger, MessagesIndex );
            }
        }
        else
        {
            Check( connection );
        }

        if ( createMessageIndex && !HasMessageIndex( connection, MessagesIndex ) )
        {
            // Validate before writing: IF NOT EXISTS must not conceal a clashing definition.
            CreatingMessageIndex( logger, MessagesIndex );
            connection.ExecuteNonQuery( MessagesIndexSql );
            CreatedMessageIndex( logger, MessagesIndex );
        }

        if ( createUpdatedMessageIndex && !HasMessageIndex( connection, UpdatedMessagesIndex ) )
        {
            CreatingMessageIndex( logger, UpdatedMessagesIndex );
            connection.ExecuteNonQuery( UpdatedMessagesIndexSql );
            CreatedMessageIndex( logger, UpdatedMessagesIndex );
        }

        CleanupSchema.Prepare( connection );
        return empty ? SchemaPreparation.Loaded : SchemaPreparation.Accepted;
    }

    private static bool HasMessageIndex( SqliteConnection connection, string name )
    {
        using var command = connection.CreateCommand( "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@name" );
        command.Parameters.AddWithValue( "@name", name );
        return ( long ) command.ExecuteScalar( )! != 0;
    }

    [LoggerMessage( EventId = 101, Level = LogLevel.Information, Message = "Creating message index {Index}; startup waits for completion" )]
    private static partial void CreatingMessageIndex( ILogger logger, string index );

    [LoggerMessage( EventId = 102, Level = LogLevel.Information, Message = "Message index {Index} created" )]
    private static partial void CreatedMessageIndex( ILogger logger, string index );

    /// <summary>Runs <c>schema.sql</c> in one immediate transaction.</summary>
    public static void Load( SqliteConnection connection, string environment, TimeProvider clock, bool createMessageIndex = true )
    {
        using var transaction = connection.BeginTransaction( );
        using var command = connection.CreateCommand( createMessageIndex ? SchemaSql : SchemaSql.Replace( MessagesIndexSql, "", StringComparison.Ordinal ) );
        command.Transaction = transaction;
        command.Parameters.AddWithValue( "@environment", environment );
        command.Parameters.Add( RailsTimestamp.Parameter( "@now", clock.GetUtcNow( ).UtcDateTime ) );
        command.ExecuteNonQuery( );
        transaction.Commit( );
    }

    /// <summary>Checks an existing database against the pinned migrations and <c>schema.sql</c>.</summary>
    /// <exception cref="SchemaMismatchException">It does not match; the message lists every difference.</exception>
    public static void Check( SqliteConnection connection )
    {
        var problems = new List<string>( );
        problems.AddRange( MigrationProblems( connection ) );
        if ( problems.Count == 0 )
        {
            problems.AddRange( CleanupSchema.Problems( connection ) );
            problems.AddRange( ShapeProblems( SchemaShape.Read( connection ) ) );
        }

        if ( problems.Count > 0 )
        {
            throw new SchemaMismatchException(
                $"The database does not match Campfire schema {PinnedVersion}: {string.Join( "; ", problems )}" );
        }
    }

    private static bool IsEmpty( SqliteConnection connection ) =>
        connection.ExecuteInt64( "SELECT COUNT(*) FROM main.sqlite_master WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\'" ) == 0;

    private static List<string> MigrationProblems( SqliteConnection connection )
    {
        if ( connection.ExecuteInt64( "SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = 'schema_migrations'" ) == 0 )
        {
            return ["it has tables but no schema_migrations table, so it is not a Campfire database"];
        }

        var versions = new HashSet<string>( StringComparer.Ordinal );
        using var command = connection.CreateCommand( "SELECT version FROM schema_migrations" );
        using ( var reader = command.ExecuteReader( ) )
        {
            while ( reader.Read( ) )
            {
                versions.Add( reader.GetString( 0 ) );
            }
        }

        var problems = new List<string>( );
        var missing = MigrationVersions.Where( version => !versions.Contains( version ) ).ToList( );
        if ( missing.Count > 0 )
        {
            problems.Add( $"migrations not run: {string.Join( ", ", missing )} (boot the Rails image once to migrate it)" );
        }

        var later = versions.Where( IsLaterThanPinned ).Order( StringComparer.Ordinal ).ToList( );
        if ( later.Count > 0 )
        {
            problems.Add( $"migrations newer than {PinnedVersion}: {string.Join( ", ", later )}" );
        }

        return problems;
    }

    private static bool IsLaterThanPinned( string version )
    {
        var pinned = long.Parse( PinnedVersion, CultureInfo.InvariantCulture );
        return !long.TryParse( version, NumberStyles.None, CultureInfo.InvariantCulture, out var number ) || number > pinned;
    }

    private static List<string> ShapeProblems( SchemaShape actual )
    {
        CleanupSchema.RemoveOwned( actual );
        var expected = Expected.Value;
        var problems = new List<string>( );
        Compare( "table", expected.Tables, actual.Tables, problems, allowedExtra: null );
        Compare( "index", expected.Indexes, actual.Indexes, problems, allowedExtra:
        [
            (MessagesIndex, MessagesIndexShape),
            (UpdatedMessagesIndex, SchemaShape.DescribeIndex( TableNames.Messages, unique: false, partial: false, ["room_id", "updated_at"] )),
        ] );
        return problems;
    }

    private static string MessagesIndexShape =>
        SchemaShape.DescribeIndex( TableNames.Messages, unique: false, partial: false, ["room_id", "created_at"] );

    private static void Compare(
        string kind,
        SortedDictionary<string, string> expected,
        SortedDictionary<string, string> actual,
        List<string> problems,
        (string Name, string Shape)[]? allowedExtra )
    {
        foreach ( var (name, shape) in expected )
        {
            if ( !actual.TryGetValue( name, out var found ) )
            {
                problems.Add( $"missing {kind} {name}" );
            }
            else if ( found != shape )
            {
                problems.Add( $"{kind} {name} differs: expected [{shape}], found [{found}]" );
            }
        }

        foreach ( var (name, shape) in actual )
        {
            var allowed = allowedExtra?.Any( extra => extra.Name == name && extra.Shape == shape ) == true;
            if ( !expected.ContainsKey( name ) && !allowed )
            {
                problems.Add( $"unexpected {kind} {name} [{shape}]" );
            }
        }
    }

    private static SchemaShape ReadExpectedShape( )
    {
        using var connection = new SqliteConnection( "Data Source=:memory:;Pooling=False" );
        connection.Open( );
        Load( connection, "check", TimeProvider.System );
        var expected = SchemaShape.Read( connection );
        expected.Indexes.Remove( MessagesIndex ); // Existing Rails databases may lack the optional index.
        return expected;
    }

    private static string LoadScript( )
    {
        using var stream = Assembly.GetExecutingAssembly( ).GetManifestResourceStream( ResourceName );
        if ( stream is null )
        {
            throw new InvalidOperationException( $"The embedded resource {ResourceName} is missing" );
        }

        using var reader = new StreamReader( stream );
        return reader.ReadToEnd( );
    }
}

/// <summary>What <see cref="SchemaSetup.Prepare"/> did.</summary>
public enum SchemaPreparation
{
    /// <summary>The database was empty; <c>schema.sql</c> was loaded.</summary>
    Loaded,

    /// <summary>The existing database matched and was accepted unchanged.</summary>
    Accepted,
}
