namespace Campfire.Application.Data;

/// <summary>Where the database lives and how many connections serve it.</summary>
public sealed class DatabaseOptions
{
    /// <summary>The storage root: the file is <c>&lt;root&gt;/db/&lt;environment&gt;.sqlite3</c>, as in <c>config/database.yml</c>.</summary>
    public string StorageRoot { get; set; } = "storage";

    /// <summary><c>RAILS_ENV</c>: names the file and is recorded in <c>ar_internal_metadata</c>.</summary>
    public string Environment { get; set; } = "production";

    /// <summary>Reader connections, and so concurrent reads. Rails' pool holds 10 connections (<c>RAILS_MAX_THREADS</c>).</summary>
    public int ReaderCount { get; set; } = 8;

    /// <summary>Disable only for an explicitly labelled unindexed comparison; never drops an existing index.</summary>
    public bool CreateMessageIndex { get; set; } = true;

    /// <summary>Creates the optional reconnect index when enabled. Existing indexes are retained.</summary>
    public bool CreateUpdatedMessageIndex { get; set; }

    public string DatabasePath => Path.Combine( StorageRoot, "db", $"{Environment}.sqlite3" );

    /// <summary>When the background checkpointer runs (<see cref="WalCheckpointer"/>); tests shrink these.</summary>
    internal CheckpointSettings Checkpoints { get; init; } = CheckpointSettings.Default;

    /// <exception cref="ArgumentException">An option is unusable.</exception>
    public void Validate( )
    {
        if ( string.IsNullOrWhiteSpace( StorageRoot ) )
        {
            throw new ArgumentException( "The storage root is required", nameof( StorageRoot ) );
        }

        if ( string.IsNullOrEmpty( Environment ) || !Environment.All( character => char.IsAsciiLetterOrDigit( character ) || character is '_' or '-' ) )
        {
            throw new ArgumentException( $"\"{Environment}\" is not a usable environment name", nameof( Environment ) );
        }

        if ( ReaderCount is < 1 or > 64 )
        {
            throw new ArgumentException( "Use between 1 and 64 reader connections", nameof( ReaderCount ) );
        }
    }
}
