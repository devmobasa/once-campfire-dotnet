using Microsoft.Data.Sqlite;

namespace Campfire.Application.Data;

/// <summary>
/// A data session: one leased SQLite connection (<see cref="CampfireDatabase"/>) and, inside a write transaction,
/// that transaction. Queries and operations take it and run explicit, parameterized SQL on it through Dapper, whose
/// call sites Dapper.AOT replaces at build time with generated code (<c>docs/development.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every statement passes <see cref="Transaction"/>, as ADO.NET expects of a command on a connection with a transaction
/// open (SQLite's transaction belongs to the connection, so a statement on the writer during a write transaction is
/// part of it either way). Outside a transaction (readers, reads between transactions, after-commit work) it is
/// <see langword="null"/> and each statement commits on its own.
/// </para>
/// <para>
/// There is no change tracking and no identity map: a row read here is a snapshot, and a write is the statement the
/// operation runs, where Rails would run it. A session lives as long as its lease: once the lease is released,
/// <see cref="Connection"/> throws, so a session kept by mistake cannot reach a connection another caller holds.
/// Sessions are not thread-safe.
/// </para>
/// </remarks>
public sealed class CampfireDb
{
    private readonly SqliteConnection _connection;

    private bool _released;

    internal CampfireDb( SqliteConnection connection, bool hasUpdatedMessageIndex )
    {
        _connection = connection;
        HasUpdatedMessageIndex = hasUpdatedMessageIndex;
    }

    /// <summary>The leased connection, with the Rails pragmas applied (<see cref="SqliteConnections"/>).</summary>
    /// <exception cref="ObjectDisposedException">The lease was released.</exception>
    public SqliteConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf( _released, this );
            return _connection;
        }
    }

    /// <summary>The validated reconnect index was present when this database opened.</summary>
    internal bool HasUpdatedMessageIndex { get; }

    /// <summary>The open <c>BEGIN IMMEDIATE</c> transaction, or <see langword="null"/> outside one.</summary>
    public SqliteTransaction? Transaction { get; internal set; }

    /// <summary>Ends the session when its lease is released.</summary>
    internal void Release( )
    {
        _released = true;
        Transaction = null;
    }
}
