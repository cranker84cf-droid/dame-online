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
            case "setAppearance":
                await HandleSetAppearance(connectionId, message.Payload.Deserialize<SetAppearanceRequest>(_jsonOptions)!);
                break;
            case "setReady":
                await HandleSetReady(connectionId, message.Payload.Deserialize<SetReadyRequest>(_jsonOptions)!);
                break;
            case "startMatch":
                await HandleStartMatch(connectionId);
                break;
            case "setDrawOffer":
                await HandleSetDrawOffer(connectionId, message.Payload.Deserialize<SetDrawOfferRequest>(_jsonOptions)!);
                break;
            case "resign":
                await HandleResign(connectionId);
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

            room.ReadyBySide[session.Side] = false;
            room.Phase = room.Sessions.Count < 2 ? "room" : "setup";
            room.PendingResolutionCts?.Cancel();
            room.PendingResolutionCts = null;
            room.ResolutionDeadlineAt = null;
            room.ContinuationRequired = false;
            room.Game = CreateGame(room);
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
            Game = new GameState(new RulesConfig()),
            ReadyBySide = new Dictionary<PlayerSide, bool>
            {
                [PlayerSide.White] = false,
                [PlayerSide.Black] = false
            },
            DrawOfferBySide = new Dictionary<PlayerSide, bool>
            {
                [PlayerSide.White] = false,
                [PlayerSide.Black] = false
            },
            AppearanceBySide = new Dictionary<PlayerSide, PlayerAppearance>
            {
                [PlayerSide.White] = new PlayerAppearance { PieceColor = "#f4f0e8", KingColor = "#f4f0e8" },
                [PlayerSide.Black] = new PlayerAppearance { PieceColor = "#202020", KingColor = "#202020" }
            },
            Phase = "room"
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
        room.Phase = "setup";

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

        if (room.Phase != "game")
        {
            await SendAsync(connectionId, "error", new { message = "Das Spiel hat noch nicht begonnen." });
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
            room.ReadyBySide[PlayerSide.White] = false;
            room.ReadyBySide[PlayerSide.Black] = false;
            room.DrawOfferBySide[PlayerSide.White] = false;
            room.DrawOfferBySide[PlayerSide.Black] = false;
            var resultMessage = room.Game.StatusMessage;
            room.Game = CreateGame(room);
            room.Phase = "setup";
            room.CountdownEndsAt = null;
            await BroadcastMessage(room, "toast", new { message = resultMessage });
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

        room.PenaltyMarker = result.PenaltyMarker;
        if (room.Phase == "game")
        {
            ScheduleResolution(room, session.Side, request.PieceId, result.ContinuationRequired);
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
        room.ReadyBySide[PlayerSide.White] = false;
        room.ReadyBySide[PlayerSide.Black] = false;
        await BroadcastSnapshot(room);
    }

    private async Task HandleSetAppearance(string connectionId, SetAppearanceRequest request)
    {
        if (!TryGetSession(connectionId, out var room, out var session))
        {
            return;
        }

        room.AppearanceBySide[session.Side] = new PlayerAppearance
        {
            PieceColor = request.PieceColor,
            KingColor = request.KingColor
        };
        room.ReadyBySide[session.Side] = false;
        await BroadcastSnapshot(room);
    }

    private async Task HandleSetReady(string connectionId, SetReadyRequest request)
    {
        if (!TryGetSession(connectionId, out var room, out var session))
        {
            return;
        }

        if (room.Phase == "countdown" || room.Phase == "game")
        {
            return;
        }

        room.ReadyBySide[session.Side] = request.Ready;
        await BroadcastSnapshot(room);
    }

    private async Task HandleSetDrawOffer(string connectionId, SetDrawOfferRequest request)
    {
        if (!TryGetSession(connectionId, out var room, out var session))
        {
            return;
        }

        if (room.Phase != "game")
        {
            return;
        }

        room.DrawOfferBySide[session.Side] = request.Offered;
        var opponent = session.Side == PlayerSide.White ? PlayerSide.Black : PlayerSide.White;
        if (request.Offered && room.DrawOfferBySide[opponent])
        {
            room.PendingResolutionCts?.Cancel();
            room.Game.EndAsDraw();
            await UpdateDrawStatsAsync(room);
            room.ReadyBySide[PlayerSide.White] = false;
            room.ReadyBySide[PlayerSide.Black] = false;
            room.DrawOfferBySide[PlayerSide.White] = false;
            room.DrawOfferBySide[PlayerSide.Black] = false;
            room.Game = CreateGame(room);
            room.Phase = "setup";
            room.CountdownEndsAt = null;
            await BroadcastMessage(room, "toast", new { message = "Remis bestaetigt. Zurueck zur Vorbereitung." });
        }

        await BroadcastSnapshot(room);
    }

    private async Task HandleResign(string connectionId)
    {
        if (!TryGetSession(connectionId, out var room, out var session))
        {
            return;
        }

        if (room.Phase != "game")
        {
            return;
        }

        room.Game.EndByResignation(session.Side);
        room.PendingResolutionCts?.Cancel();
        await UpdateStatsAfterGameAsync(room, session.Name);
        room.ReadyBySide[PlayerSide.White] = false;
        room.ReadyBySide[PlayerSide.Black] = false;
        room.DrawOfferBySide[PlayerSide.White] = false;
        room.DrawOfferBySide[PlayerSide.Black] = false;
        room.Game = CreateGame(room);
        room.Phase = "setup";
        room.CountdownEndsAt = null;
        await BroadcastMessage(room, "toast", new { message = "Aufgabe bestaetigt. Zurueck zur Vorbereitung." });
        await BroadcastSnapshot(room);
    }

    private async Task HandleStartMatch(string connectionId)
    {
        if (!TryGetSession(connectionId, out var room, out var session))
        {
            return;
        }

        if (room.HostConnectionId != connectionId)
        {
            await SendAsync(connectionId, "error", new { message = "Nur der Host kann das Spiel starten." });
            return;
        }

        if (room.Sessions.Count < 2)
        {
            await SendAsync(connectionId, "error", new { message = "Es werden zwei Spieler benoetigt." });
            return;
        }

        if (room.ReadyBySide.Values.Any(ready => !ready))
        {
            await SendAsync(connectionId, "error", new { message = "Beide Spieler muessen bereit sein." });
            return;
        }

        room.Game = CreateGame(room);
        room.Game.SetStartingPlayer(Random.Shared.Next(2) == 0 ? PlayerSide.White : PlayerSide.Black);
        room.PendingResolutionCts?.Cancel();
        room.DrawOfferBySide[PlayerSide.White] = false;
        room.DrawOfferBySide[PlayerSide.Black] = false;
        room.Phase = "countdown";
        room.ResolutionDeadlineAt = null;
        room.ContinuationRequired = false;
        room.PenaltyMarker = null;
        room.CountdownEndsAt = DateTimeOffset.UtcNow.AddSeconds(5);
        await BroadcastSnapshot(room);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            room.Phase = "game";
            room.CountdownEndsAt = null;
            await BroadcastSnapshot(room);
        });
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

    private async Task UpdateDrawStatsAsync(RoomState room)
    {
        foreach (var side in new[] { PlayerSide.White, PlayerSide.Black })
        {
            var name = room.Game.Players[side];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            await _playerStore.UpdateAsync(name, stats => { stats.TotalGames++; });
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

        var snapshot = room.Game.ToSnapshot(
            room.Code,
            room.HostName,
            room.Phase,
            stats,
            room.ReadyBySide,
            room.DrawOfferBySide,
            room.AppearanceBySide,
            room.CountdownEndsAt,
            room.ResolutionDeadlineAt,
            room.ContinuationRequired,
            room.PenaltyMarker);
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

    private static GameState CreateGame(RoomState room)
    {
        var game = new GameState(room.Rules);
        game.SetPlayers(
            room.Sessions.Values.FirstOrDefault(s => s.Side == PlayerSide.White)?.Name ?? "",
            room.Sessions.Values.FirstOrDefault(s => s.Side == PlayerSide.Black)?.Name ?? "");
        return game;
    }

    private void ScheduleResolution(RoomState room, PlayerSide player, string pieceId, bool continuationRequired)
    {
        room.PendingResolutionCts?.Cancel();
        room.PendingResolutionCts?.Dispose();
        var cts = new CancellationTokenSource();
        room.PendingResolutionCts = cts;
        room.ContinuationRequired = continuationRequired;
        room.ResolutionDeadlineAt = DateTimeOffset.UtcNow.AddSeconds(3);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                MoveResult result;
                if (continuationRequired)
                {
                    result = room.Game.ResolveMissedContinuation(player, pieceId);
                }
                else
                {
                    result = room.Game.FinalizeResolvedTurn();
                }

                room.ResolutionDeadlineAt = null;
                room.ContinuationRequired = false;
                room.PenaltyMarker = result.PenaltyMarker;

                if (room.Game.IsGameOver)
                {
                    await UpdateStatsAfterGameAsync(room, room.Game.Players[player]);
                    room.ReadyBySide[PlayerSide.White] = false;
                    room.ReadyBySide[PlayerSide.Black] = false;
                    room.DrawOfferBySide[PlayerSide.White] = false;
                    room.DrawOfferBySide[PlayerSide.Black] = false;
                    var resultMessage = room.Game.StatusMessage;
                    room.Game = CreateGame(room);
                    room.Phase = "setup";
                    room.CountdownEndsAt = null;
                    await BroadcastMessage(room, "toast", new { message = resultMessage });
                }

                await BroadcastSnapshot(room);
            }
            catch (TaskCanceledException)
            {
            }
        });
    }

    public IReadOnlyList<OpenRoomInfo> GetOpenRooms()
    {
        return _rooms.Values
            .Where(room => room.Sessions.Count < 2)
            .OrderBy(room => room.Code)
            .Select(room => new OpenRoomInfo
            {
                RoomCode = room.Code,
                HostName = room.HostName,
                PlayerCount = room.Sessions.Count
            })
            .ToList();
    }
}
