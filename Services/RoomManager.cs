using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CheckersOnline;

namespace CheckersOnline.Services;

public sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, RoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly PlayerStore _playerStore;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RoomManager(PlayerStore playerStore)
    {
        _playerStore = playerStore;
    }

    public async Task RegisterSocketAsync(string connectionId, WebSocket socket)
    {
        _sockets[connectionId] = socket;
        await Task.CompletedTask;
    }

    public async Task HandleMessageAsync(string connectionId, ClientEnvelope message)
    {
        switch (message.Type)
        {
            case "createRoom":
                await HandleCreateRoom(connectionId, message.Payload.Deserialize<CreateRoomRequest>(_jsonOptions)!);
                break;
            case "joinRoom":
                await HandleJoinRoom(connectionId, message.Payload.Deserialize<JoinRoomRequest>(_jsonOptions)!);
                break;
            case "selectPiece":
                await HandleSelectPiece(connectionId, message.Payload.Deserialize<SelectPieceRequest>(_jsonOptions)!);
                break;
            case "move":
                await HandleMove(connectionId, message.Payload.Deserialize<MoveCommand>(_jsonOptions)!);
                break;
            case "updateRules":
                await HandleUpdateRules(connectionId, message.Payload.Deserialize<UpdateRulesRequest>(_jsonOptions)!);
                break;
            case "ping":
                await SendAsync(connectionId, "pong", new { ok = true });
                break;
            default:
                await SendAsync(connectionId, "error", new { message = "Unbekannter Nachrichtentyp." });
                break;
        }
    }

    public async Task DisconnectAsync(string connectionId)
    {
        _sockets.TryRemove(connectionId, out _);
        await _gate.WaitAsync();
        try
        {
            var room = _rooms.Values.FirstOrDefault(r => r.Sessions.ContainsKey(connectionId));
            if (room is null)
            {
                return;
            }

            var session = room.Sessions[connectionId];
            room.Sessions.Remove(connectionId);
            if (room.Sessions.Count == 0)
            {
                _rooms.TryRemove(room.Code, out _);
                return;
            }

            room.Game.UpdateRules(room.Rules);
            room.Game.SetPlayers(
                room.Sessions.Values.FirstOrDefault(s => s.Side == PlayerSide.White)?.Name ?? "",
                room.Sessions.Values.FirstOrDefault(s => s.Side == PlayerSide.Black)?.Name ?? "");
            await BroadcastSnapshot(room);
            await BroadcastMessage(room, "toast", new { message = $"{session.Name} hat den Raum verlassen." });
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleCreateRoom(string connectionId, CreateRoomRequest request)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await SendAsync(connectionId, "error", new { message = "Bitte einen Namen angeben." });
            return;
        }

        var code = CreateCode();
        await _playerStore.GetOrCreateAsync(name);

        var room = new RoomState
        {
            Code = code,
            HostConnectionId = connectionId,
            HostName = name,
            Rules = new RulesConfig(),
            Sessions = new Dictionary<string, PlayerSession>(),
            Game = new GameState(new RulesConfig())
        };

        room.Sessions[connectionId] = new PlayerSession
        {
            ConnectionId = connectionId,
            Name = name,
            RoomCode = code,
            Side = PlayerSide.White
        };
        room.Game.SetPlayers(name, "");
        _rooms[code] = room;

        await SendAsync(connectionId, "roomJoined", new
        {
            roomCode = code,
            side = PlayerSide.White,
            isHost = true
        });
        await BroadcastSnapshot(room);
    }

    private async Task HandleJoinRoom(string connectionId, JoinRoomRequest request)
    {
        var name = request.Name.Trim();
        var code = request.RoomCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
        {
            await SendAsync(connectionId, "error", new { message = "Name und Raumcode werden benoetigt." });
            return;
        }

        if (!_rooms.TryGetValue(code, out var room))
        {
            await SendAsync(connectionId, "error", new { message = "Raum nicht gefunden." });
            return;
        }

        if (room.Sessions.Count >= 2)
        {
            await SendAsync(connectionId, "error", new { message = "Dieser Raum ist bereits voll." });
            return;
        }

        await _playerStore.GetOrCreateAsync(name);
        room.Sessions[connectionId] = new PlayerSession
        {
            ConnectionId = connectionId,
            Name = name,
            RoomCode = code,
            Side = PlayerSide.Black
        };
        room.Game.SetPlayers(
            room.Sessions.Values.First(s => s.Side == PlayerSide.White).Name,
            name);

        await SendAsync(connectionId, "roomJoined", new
        {
            roomCode = code,
            side = PlayerSide.Black,
            isHost = false
        });
        await BroadcastMessage(room, "toast", new { message = $"{name} ist dem Raum beigetreten." });
        await BroadcastSnapshot(room);
    }

    private async Task HandleSelectPiece(string connectionId, SelectPieceRequest request)
    {
        if (!TryGetSession(connectionId, out var room, out var session))
        {
            return;
        }

        room.Game.SelectPiece(request.PieceId, session.Side);
        await BroadcastSnapshot(room);
    }

    private async Task HandleMove(string connectionId, MoveCommand request)
    {
        if (!TryGetSession(connectionId, out var room, out var session))
        {
            return;
        }

        var result = room.Game.ApplyMove(session.Side, request);
        if (!result.Success)
        {
            await SendAsync(connectionId, "error", new { message = result.Message });
            await BroadcastSnapshot(room);
            return;
        }

        if (room.Game.IsGameOver)
        {
            await UpdateStatsAfterGameAsync(room, session.Name);
        }
        else if (room.Game.LastMoveDurationMs > 0)
        {
            await _playerStore.UpdateAsync(session.Name, stats =>
            {
                stats.BestMoveTimeMs = stats.BestMoveTimeMs is null
                    ? room.Game.LastMoveDurationMs
                    : Math.Min(stats.BestMoveTimeMs.Value, room.Game.LastMoveDurationMs);
            });
        }

        await BroadcastSnapshot(room);
    }

    private async Task HandleUpdateRules(string connectionId, UpdateRulesRequest request)
    {
        if (!TryGetSession(connectionId, out var room, out var session))
        {
            return;
        }

        if (room.HostConnectionId != connectionId)
        {
            await SendAsync(connectionId, "error", new { message = "Nur der Host darf die Regeln aendern." });
            return;
        }

        room.Rules = request.Rules;
        room.Game.UpdateRules(request.Rules);
        await BroadcastSnapshot(room);
    }

    private bool TryGetSession(string connectionId, out RoomState room, out PlayerSession session)
    {
        room = _rooms.Values.FirstOrDefault(r => r.Sessions.ContainsKey(connectionId))!;
        session = null!;
        if (room is null)
        {
            return false;
        }

        session = room.Sessions[connectionId];
        return true;
    }

    private async Task UpdateStatsAfterGameAsync(RoomState room, string lastMover)
    {
        var winnerIsWhite = room.Game.StatusMessage.Contains(room.Game.Players[PlayerSide.White], StringComparison.Ordinal);
        var winner = winnerIsWhite ? room.Game.Players[PlayerSide.White] : room.Game.Players[PlayerSide.Black];
        var loser = winnerIsWhite ? room.Game.Players[PlayerSide.Black] : room.Game.Players[PlayerSide.White];

        if (!string.IsNullOrWhiteSpace(winner))
        {
            await _playerStore.UpdateAsync(winner, stats =>
            {
                stats.TotalGames++;
                stats.Wins++;
                if (room.Game.LastMoveDurationMs > 0)
                {
                    stats.BestMoveTimeMs = stats.BestMoveTimeMs is null
                        ? room.Game.LastMoveDurationMs
                        : Math.Min(stats.BestMoveTimeMs.Value, room.Game.LastMoveDurationMs);
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(loser))
        {
            await _playerStore.UpdateAsync(loser, stats =>
            {
                stats.TotalGames++;
                stats.Losses++;
            });
        }
    }

    private async Task BroadcastSnapshot(RoomState room)
    {
        var stats = new Dictionary<PlayerSide, PlayerStats>();
        foreach (var side in new[] { PlayerSide.White, PlayerSide.Black })
        {
            var playerName = room.Game.Players[side];
            if (string.IsNullOrWhiteSpace(playerName))
            {
                stats[side] = new PlayerStats();
                continue;
            }

            stats[side] = (await _playerStore.GetOrCreateAsync(playerName)).Stats;
        }

        var snapshot = room.Game.ToSnapshot(room.Code, room.HostName, stats);
        await BroadcastMessage(room, "snapshot", snapshot);
    }

    private async Task BroadcastMessage(RoomState room, string type, object payload)
    {
        foreach (var connectionId in room.Sessions.Keys.ToList())
        {
            await SendAsync(connectionId, type, payload);
        }
    }

    private async Task SendAsync(string connectionId, string type, object payload)
    {
        if (!_sockets.TryGetValue(connectionId, out var socket) || socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new { type, payload }, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static string CreateCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
