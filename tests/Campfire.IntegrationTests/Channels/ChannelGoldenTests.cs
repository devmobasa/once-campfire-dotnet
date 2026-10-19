using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.Application.Memberships;
using Campfire.Application.Rooms;
using Campfire.Application.Users;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Tests.Cable.Support;
using Campfire.Web.Workers;
using Microsoft.Data.Sqlite;

namespace Campfire.IntegrationTests.Channels;

/// <summary>
/// Frame sequences recorded from the Rails app's channels (<c>vectors/channels/reference.json</c>, recorded by Rust
/// <c>crates/campfire/src/channels/tests/golden.rs</c> at <c>3fe9f61</c> against the reference container, with
/// <c>fixtures.rb</c> and <c>trigger.rb</c> beside it), replayed against the whole app over the same rows. Fixture rows
/// come from the recording; sessions and signed stream names are minted by the native app for each replay. Server-side
/// events (an unread fan-out, a message removal, revoking a membership, deactivating a user)
/// run through the Application operations <c>trigger.rb</c>'s Ruby calls map to.
/// </summary>
/// <remarks>
/// As in the Rust replay, each step's frames are compared per socket as a sorted list (a confirmation and a broadcast
/// that reach one socket in one step race in Rails, where the confirmation waits for Redis), and pings are dropped.
/// Every frame must match byte for byte.
/// </remarks>
public sealed class ChannelGoldenTests
{
    /// <summary>How long a socket must stay quiet, once it has its recorded frames, for extra frames to count as none.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds( 150 );

    /// <summary>How long to wait for a socket's recorded frames.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds( 5 );

    [Fact]
    public async Task Replays_the_reference_frames_for_every_channel( )
    {
        var golden = RepositoryPaths.ReadVectorJson( "channels", "reference.json" );
        var fixtures = golden["fixtures"]!;
        await using var factory = new ChannelGoldenAppFactory( fixtures["rows"]!.AsObject( ) );
        _ = factory.App; // starts the server
        await using var replay = await Replay.CreateAsync( factory, fixtures );
        var expected = golden["steps"]!.AsArray( ).Select( step => Exchange.From( step, replay.RecordedTokens, replay.Tokens ) ).ToList( );

        var script = Script( replay.Tokens );
        Assert.Equal( expected.Select( step => step.Step ), script.Select( step => step.Name ) );

        for ( var index = 0; index < script.Count; index++ )
        {
            var step = expected[index];
            await script[index].Run( replay );
            var actual = await replay.CollectAsync( step );
            Assert.True( step.Matches( actual ), $"step {index} \"{step.Step}\":\nexpected {step}\nactual   {new Exchange( step.Step, actual )}" );
        }
    }

    private sealed record ScriptStep( string Name, Func<Replay, Task> Run );

    /// <summary><c>script</c> in the Rust recorder: what each socket sends, and the server-side events, in order.</summary>
    private static List<ScriptStep> Script( Dictionary<string, string> tokens )
    {
        var roomId = long.Parse( tokens["ROOM_ID"], CultureInfo.InvariantCulture );
        var closedId = long.Parse( tokens["CLOSED_ID"], CultureInfo.InvariantCulture );

        // serde_json's json! writes keys in sorted order; the identifier is echoed back byte for byte.
        var reads = Identifier( ("channel", "ReadRoomsChannel") );
        var unreads = Identifier( ("channel", "UnreadRoomsChannel") );
        var presence = Identifier( ("channel", "PresenceChannel"), ("room_id", roomId) );
        var room = Identifier( ("channel", "RoomChannel"), ("room_id", roomId) );
        var typing = Identifier( ("channel", "TypingNotificationsChannel"), ("room_id", roomId) );
        var messages = Identifier( ("channel", "RoomMessagesChannel"), ("signed_stream_name", tokens["ROOM_MESSAGES_SIGNED"]) );
        string Turbo( string signed ) => Identifier( ("channel", "Turbo::StreamsChannel"), ("signed_stream_name", signed) );
        var guarded = Turbo( tokens["ROOM_MESSAGES_SIGNED"] );
        var rooms = Turbo( tokens["ROOMS_SIGNED"] );
        const string Forged = "InJvb21zIg==--0000";

        static ScriptStep Connect( string name, string socket, string? cookie ) =>
            new( name, replay => replay.ConnectAsync( socket, cookie ) );
        static ScriptStep Subscribe( string name, string socket, string identifier ) =>
            new( name, replay => replay.SendAsync( socket, "subscribe", identifier ) );
        static ScriptStep Perform( string name, string socket, string identifier, JsonObject data ) =>
            new( name, replay => replay.SendAsync( socket, "message", identifier, data ) );
        static ScriptStep Trigger( string name, Func<Replay, Task> run ) => new( name, run );

        return
        [
            Connect( "connect A", "A", "A" ),
            Connect( "connect B", "B", "B" ),
            Connect( "connect without a cookie", "X", null ),
            Subscribe( "A heartbeat", "A", Identifier( ("channel", "HeartbeatChannel") ) ),
            Subscribe( "A base channel", "A", Identifier( ("channel", "ApplicationCable::Channel") ) ),
            Subscribe( "A reads", "A", reads ),
            Subscribe( "A unreads", "A", unreads ),
            Subscribe( "A presence", "A", presence ),
            Subscribe( "A presence in a room A isn't in", "A", Identifier( ("channel", "PresenceChannel"), ("room_id", closedId) ) ),
            Subscribe( "A presence with a non-numeric room", "A", Identifier( ("channel", "PresenceChannel"), ("room_id", "abc") ) ),
            Subscribe(
                "A presence with a numeric string room",
                "A",
                Identifier( ("channel", "PresenceChannel"), ("room_id", roomId.ToString( CultureInfo.InvariantCulture )) ) ),
            Subscribe( "A room", "A", room ),
            Subscribe( "A room A isn't in", "A", Identifier( ("channel", "RoomChannel"), ("room_id", closedId) ) ),
            Subscribe( "A room without an id", "A", Identifier( ("channel", "RoomChannel") ) ),
            Subscribe( "A typing", "A", typing ),
            Subscribe( "B typing", "B", typing ),
            Perform( "A starts typing", "A", typing, new JsonObject { ["action"] = "start" } ),
            Perform( "B stops typing", "B", typing, new JsonObject { ["action"] = "stop", ["extra"] = 1 } ),
            Perform( "A performs an unknown typing action", "A", typing, new JsonObject { ["action"] = "dance" } ),
            Subscribe( "A room messages", "A", messages ),
            Subscribe(
                "A room messages for a room A isn't in",
                "A",
                Identifier( ("channel", "RoomMessagesChannel"), ("signed_stream_name", tokens["CLOSED_MESSAGES_SIGNED"]) ) ),
            Subscribe( "A room messages without a name", "A", Identifier( ("channel", "RoomMessagesChannel") ) ),
            Subscribe( "A room messages with a forged name", "A", Identifier( ("channel", "RoomMessagesChannel"), ("signed_stream_name", Forged) ) ),
            Subscribe(
                "A room messages with the rooms name",
                "A",
                Identifier( ("channel", "RoomMessagesChannel"), ("signed_stream_name", tokens["ROOMS_SIGNED"]) ) ),
            Subscribe( "A turbo rooms", "A", rooms ),
            Subscribe( "A turbo own rooms", "A", Turbo( tokens["A_ROOMS_SIGNED"] ) ),
            Subscribe( "A turbo guarded room messages", "A", guarded ),
            Subscribe( "A turbo forged", "A", Turbo( Forged ) ),
            Perform( "A presence present", "A", presence, new JsonObject { ["action"] = "present" } ),
            Perform( "A presence refresh", "A", presence, new JsonObject { ["action"] = "refresh" } ),
            Perform( "A unreads subscribed again", "A", unreads, new JsonObject { ["action"] = "subscribed" } ),
            Trigger( "unread fanout", replay => replay.UnreadFanoutAsync( ) ),
            Trigger( "message removed", replay => replay.RemoveMessageAsync( ) ),
            Subscribe( "B presence", "B", presence ),
            Perform( "A typing again", "A", typing, new JsonObject { ["action"] = "start" } ),
            Trigger( "revoke A", replay => replay.RevokeAsync( roomId, replay.Id( "A_ID" ) ) ),
            Connect( "reconnect A", "A2", "A" ),
            Subscribe( "A2 presence", "A2", presence ),
            Subscribe( "A2 room", "A2", room ),
            Subscribe( "A2 typing", "A2", typing ),
            Subscribe( "A2 room messages", "A2", messages ),
            Subscribe( "A2 turbo guarded room messages", "A2", guarded ),
            Subscribe( "A2 turbo rooms", "A2", rooms ),
            Subscribe( "A2 unreads", "A2", unreads ),
            Perform( "B typing after A left", "B", typing, new JsonObject { ["action"] = "start" } ),
            Trigger( "deactivate B", replay => replay.DeactivateAsync( replay.Id( "B_ID" ) ) ),
            Connect( "reconnect B", "B2", "B" ),
        ];
    }

    private static string Identifier( params (string Key, JsonNode? Value)[] pairs ) => CableTestClient.Identifier( pairs );

    /// <summary>One recorded step: socket name → the frames it received, sorted (quiet sockets left out).</summary>
    private sealed record Exchange( string Step, IReadOnlyDictionary<string, List<string>> Frames )
    {
        public static Exchange From(
            JsonNode? step,
            IReadOnlyDictionary<string, string> recordedTokens,
            IReadOnlyDictionary<string, string> nativeTokens )
        {
            var name = step!["step"]!.GetValue<string>( );
            var frames = step["frames"]!.AsObject( ).ToDictionary(
                socket => socket.Key,
                socket => socket.Value!.AsArray( )
                    .Select( frame => CurrentRoomNotice( ReplaceRecordedStreamTokens( frame!.GetValue<string>( ), recordedTokens, nativeTokens ) ) )
                    .Order( StringComparer.Ordinal )
                    .ToList( ),
                StringComparer.Ordinal );

            return new Exchange( name, frames );
        }

        // The historical vector stays intact. Current Rails adds numeric seconds to room notices.
        private static string CurrentRoomNotice( string frame )
        {
            if ( frame.Contains( "\"message\":{\"room_id\":", StringComparison.Ordinal ) )
            {
                return frame[..^2] + ",\"at\":1790428492}}";
            }

            if ( frame.Contains( "\"message\":{\"roomId\":", StringComparison.Ordinal ) )
            {
                return frame[..^2] + ",\"at\":1790428155.920972}}";
            }

            return frame;
        }

        /// <summary>The number of frames <paramref name="socket"/> received in this step.</summary>
        public int FrameCount( string socket ) => Frames.TryGetValue( socket, out var frames ) ? frames.Count : 0;

        /// <summary>Whether <paramref name="actual"/> holds exactly these sockets, each with exactly these frames.</summary>
        public bool Matches( Dictionary<string, List<string>> actual ) =>
            Frames.Count == actual.Count
            && Frames.All( pair => actual.TryGetValue( pair.Key, out var frames ) && frames.SequenceEqual( pair.Value ) );

        private static string ReplaceRecordedStreamTokens(
            string frame,
            IReadOnlyDictionary<string, string> recorded,
            IReadOnlyDictionary<string, string> native )
        {
            foreach ( var key in new[] { "ROOMS_SIGNED", "A_ROOMS_SIGNED", "ROOM_MESSAGES_SIGNED", "CLOSED_MESSAGES_SIGNED" } )
            {
                frame = frame.Replace( recorded[key], native[key], StringComparison.Ordinal );
            }

            return frame;
        }

        public override string ToString( ) =>
            string.Join(
                "; ",
                Frames.OrderBy( pair => pair.Key, StringComparer.Ordinal ).Select( pair => $"{pair.Key}: [{string.Join( " | ", pair.Value )}]" ) );
    }

    /// <summary>The sockets of one replay, and the server-side events it triggers.</summary>
    private sealed class Replay( ChannelGoldenAppFactory factory, JsonNode fixtures ) : IAsyncDisposable
    {
        private readonly Dictionary<string, CableTestClient> _sockets = new( StringComparer.Ordinal );
        private readonly List<SliceClient> _cookieClients = [];
        private readonly Dictionary<string, string> _cookies = new( StringComparer.Ordinal );

        /// <summary>The recorded tokens, with the signed stream names replaced by the ones the native app mints.</summary>
        public Dictionary<string, string> Tokens { get; } = RecordedTokensOf( fixtures );

        public Dictionary<string, string> RecordedTokens { get; } = RecordedTokensOf( fixtures );

        private JsonObject Message { get; } = fixtures["rows"]!["messages"]!.AsArray( ).Single( )!.AsObject( );

        public static async Task<Replay> CreateAsync( ChannelGoldenAppFactory factory, JsonNode fixtures )
        {
            var replay = new Replay( factory, fixtures );
            await replay.InitializeAsync( );
            return replay;
        }

        private async Task InitializeAsync( )
        {
            foreach ( var (label, tokenName) in new[] { ("A", "A_ID"), ("B", "B_ID") } )
            {
                var client = factory.Client( );
                client.SignInAs( factory, Id( tokenName ) );
                _cookies[label] = client.CookieHeader;
                _cookieClients.Add( client );
            }

            var app = factory.App;
            var roomId = Id( "ROOM_ID" );
            var closedId = Id( "CLOSED_ID" );
            var room = await app.Database.ReadAsync( db => RoomQueries.FindById( db, roomId )!, CancellationToken.None );
            var closedRoom = await app.Database.ReadAsync( db => RoomQueries.FindById( db, closedId )!, CancellationToken.None );
            Tokens["ROOMS_SIGNED"] = app.Broadcasts.SignedRooms( );
            Tokens["A_ROOMS_SIGNED"] = app.Broadcasts.SignedUserRooms( Id( "A_ID" ) );
            Tokens["ROOM_MESSAGES_SIGNED"] = app.Broadcasts.SignedRoomMessages( room );
            Tokens["CLOSED_MESSAGES_SIGNED"] = app.Broadcasts.SignedRoomMessages( closedRoom );
        }

        public long Id( string token ) => long.Parse( Tokens[token], CultureInfo.InvariantCulture );

        /// <summary>Opens <paramref name="socket"/> with the session cookie of <paramref name="cookie"/> (A or B), or none.</summary>
        public async Task ConnectAsync( string socket, string? cookie )
        {
            var cookieHeader = cookie is null ? null : _cookies[cookie];
            _sockets[socket] = await CableTestClient.ConnectAsync( factory.CableAddress, factory.Origin, cookieHeader );
        }

        public Task SendAsync( string socket, string command, string identifier, JsonObject? data = null )
        {
            var frame = new JsonObject { ["command"] = command, ["identifier"] = identifier };
            if ( data is not null )
            {
                frame["data"] = data.ToJsonString( CableTestClient.JsonOptions );
            }

            return _sockets[socket].SendAsync( frame );
        }

        /// <summary>Runs the current unread-room handler against the recorded message and current memberships.</summary>
        public Task UnreadFanoutAsync( ) =>
            new BroadcastUnreadRoomHandler( factory.App ).PerformAsync(
                new BroadcastUnreadRoomJob( Message["id"]!.GetValue<long>( ) ), TestContext.Current.CancellationToken );

        /// <summary><c>Message#broadcast_remove</c>.</summary>
        public async Task RemoveMessageAsync( )
        {
            var roomId = Message["room_id"]!.GetValue<long>( );
            var room = await factory.App.Database.ReadAsync( db => RoomQueries.FindById( db, roomId )!, CancellationToken.None );
            factory.App.Broadcasts.MessageRemoved( room, Message["client_message_id"]!.GetValue<string>( ) );
        }

        /// <summary><c>Room.find(id).memberships.revoke_from(User.find(id))</c>.</summary>
        public Task RevokeAsync( long roomId, long userId ) =>
            factory.App.Database.WriteAsync( scope => RoomMemberships.RevokeFrom( scope, roomId, [userId] ), CancellationToken.None );

        /// <summary><c>User.find(id).deactivate</c>.</summary>
        public Task<User> DeactivateAsync( long userId ) =>
            factory.App.Database.WriteAsync( scope => UserOperations.Deactivate( scope, userId ), CancellationToken.None );

        /// <summary>Every open socket's frames for this step, collected side by side.</summary>
        public async Task<Dictionary<string, List<string>>> CollectAsync( Exchange expected )
        {
            var collected = await Task.WhenAll( _sockets.Select( async pair =>
                (pair.Key, Frames: await CollectAsync( pair.Value, expected.FrameCount( pair.Key ) )) ) );

            return collected
                .Where( socket => socket.Frames.Count > 0 )
                .ToDictionary( socket => socket.Key, socket => socket.Frames.Order( StringComparer.Ordinal ).ToList( ), StringComparer.Ordinal );
        }

        public async ValueTask DisposeAsync( )
        {
            foreach ( var socket in _sockets.Values )
            {
                await socket.DisposeAsync( );
            }

            _sockets.Clear( );
            foreach ( var client in _cookieClients )
            {
                client.Dispose( );
            }

            _cookieClients.Clear( );
        }

        private static Dictionary<string, string> RecordedTokensOf( JsonNode fixtures ) =>
            fixtures["tokens"]!.AsObject( ).ToDictionary( token => token.Key, token => token.Value!.GetValue<string>( ), StringComparer.Ordinal );

        /// <summary>
        /// Frames until the recorded number arrived (or the patience ran out) and the socket then stayed quiet; pings
        /// dropped, and they don't extend the wait.
        /// </summary>
        private static async Task<List<string>> CollectAsync( CableTestClient socket, int expected )
        {
            var frames = new List<string>( );
            var deadline = DateTime.UtcNow + Patience;
            while ( true )
            {
                var remaining = deadline - DateTime.UtcNow;
                var wait = frames.Count < expected && remaining > Quiet ? remaining : Quiet;
                ReceivedFrame frame;
                try
                {
                    frame = await socket.NextIncludingPingsAsync( wait );
                }
                catch ( OperationCanceledException )
                {
                    return frames;
                }

                if ( frame.Kind == "end" )
                {
                    return frames;
                }

                if ( !frame.IsPing )
                {
                    frames.Add( frame.ToString( ) );
                }
            }
        }
    }

    /// <summary>
    /// The whole app over a database holding exactly the rows <c>fixtures.rb</c> printed (users, rooms, memberships,
    /// the two sessions and the message), inserted as recorded.
    /// </summary>
    private sealed class ChannelGoldenAppFactory( JsonObject rows ) : SliceAppFactory( ( connection, _ ) => Insert( connection, rows ) )
    {
        private static readonly string[] Tables = ["users", "rooms", "memberships", "sessions", "messages"];

        private static void Insert( SqliteConnection connection, JsonObject rows )
        {
            using var transaction = connection.BeginTransaction( );
            foreach ( var table in Tables )
            {
                foreach ( var row in rows[table]!.AsArray( ) )
                {
                    InsertRow( connection, transaction, table, row!.AsObject( ) );
                }
            }

            transaction.Commit( );
        }

        private static void InsertRow( SqliteConnection connection, SqliteTransaction transaction, string table, JsonObject row )
        {
            using var command = connection.CreateCommand( );
            command.Transaction = transaction;
            var columns = row.Select( column => column.Key ).ToList( );
            var quotedColumns = string.Join( ", ", columns.Select( column => $"\"{column}\"" ) );
            var placeholders = string.Join( ", ", columns.Select( ( _, index ) => $"$p{index}" ) );
            command.CommandText = $"INSERT INTO {table} ({quotedColumns}) VALUES ({placeholders})";
            for ( var index = 0; index < columns.Count; index++ )
            {
                command.Parameters.AddWithValue( $"$p{index}", SqlValue( row[columns[index]] ) );
            }

            command.ExecuteNonQuery( );
        }

        private static object SqlValue( JsonNode? value ) => value switch
        {
            null => DBNull.Value,
            JsonValue number when number.GetValueKind( ) == JsonValueKind.Number => NumberValue( number ),
            JsonValue flag when flag.GetValueKind( ) is JsonValueKind.True or JsonValueKind.False => flag.GetValue<bool>( ) ? 1L : 0L,
            JsonValue text when text.GetValueKind( ) == JsonValueKind.String => text.GetValue<string>( ),
            _ => value.ToJsonString( ),
        };

        /// <summary>A whole number as an integer (a conditional of long and double would make every number a REAL).</summary>
        private static object NumberValue( JsonValue number )
        {
            if ( number.TryGetValue<long>( out var integer ) )
            {
                return integer;
            }

            return number.GetValue<double>( );
        }
    }
}
