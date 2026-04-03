namespace CheckersOnline;

public sealed class GameState
{
    private readonly Dictionary<string, (int Row, int Col, Piece Piece)> _pieces = new();
    private readonly List<Piece> _capturedWhite = [];
    private readonly List<Piece> _capturedBlack = [];
    private readonly Dictionary<PlayerSide, long> _remainingTurnMs = new()
    {
        [PlayerSide.White] = 0,
        [PlayerSide.Black] = 0
    };

    private DateTimeOffset _turnStartedAt = DateTimeOffset.UtcNow;
    private bool _statsCommitted;

    public GameState(RulesConfig rules)
    {
        Rules = CloneRules(rules);
        CurrentTurn = PlayerSide.White;
        StatusMessage = "Warte auf beide Spieler.";
        SetupBoard();
    }

    public RulesConfig Rules { get; private set; }
    public PlayerSide CurrentTurn { get; private set; }
    public bool IsGameOver { get; private set; }
    public string StatusMessage { get; private set; }
    public string? SelectedPieceId { get; private set; }
    public long LastMoveDurationMs { get; private set; }
    public Dictionary<PlayerSide, string> Players { get; } = new()
    {
        [PlayerSide.White] = "",
        [PlayerSide.Black] = ""
    };

    public void SetPlayers(string white, string black)
    {
        Players[PlayerSide.White] = white;
        Players[PlayerSide.Black] = black;
        StatusMessage = $"Am Zug: {Players[CurrentTurn]}";
        _turnStartedAt = DateTimeOffset.UtcNow;
    }

    public void SetStartingPlayer(PlayerSide side)
    {
        CurrentTurn = side;
        StatusMessage = $"Am Zug: {Players[CurrentTurn]}";
        _turnStartedAt = DateTimeOffset.UtcNow;
    }

    public void EndByResignation(PlayerSide loser)
    {
        var winner = loser == PlayerSide.White ? PlayerSide.Black : PlayerSide.White;
        DeclareWinner(winner, $"{Players[loser]} gibt auf. {Players[winner]} gewinnt.");
    }

    public void EndAsDraw()
    {
        IsGameOver = true;
        SelectedPieceId = null;
        StatusMessage = "Remis nach Zustimmung beider Spieler.";
    }

    public void UpdateRules(RulesConfig rules)
    {
        Rules = CloneRules(rules);
        SelectedPieceId = null;
    }

    public IReadOnlyDictionary<string, (int Row, int Col, Piece Piece)> Pieces => _pieces;

    public void SelectPiece(string pieceId, PlayerSide player)
    {
        if (IsGameOver || player != CurrentTurn)
        {
            return;
        }

        if (!_pieces.TryGetValue(pieceId, out var entry) || entry.Piece.Side != player)
        {
            return;
        }

        if (SelectedPieceId is not null && SelectedPieceId != pieceId)
        {
            return;
        }

        var hasLegalMove = GetLegalMoves(player).Any(m => m.PieceId == pieceId);
        if (hasLegalMove)
        {
            SelectedPieceId = pieceId;
        }
    }

    public IReadOnlyList<Position> GetForcedDestinations(PlayerSide player)
    {
        if (player != CurrentTurn || SelectedPieceId is null)
        {
            return [];
        }

        return GetLegalMoves(player)
            .Where(m => m.PieceId == SelectedPieceId)
            .Select(m => m.Path[^1])
            .ToList();
    }

    public MoveResult ApplyMove(PlayerSide player, MoveCommand command)
    {
        if (IsGameOver)
        {
            return new MoveResult { Success = false, Message = "Das Spiel ist bereits beendet." };
        }

        if (player != CurrentTurn)
        {
            return new MoveResult { Success = false, Message = "Du bist nicht am Zug." };
        }

        if (SelectedPieceId is not null && SelectedPieceId != command.PieceId)
        {
            return new MoveResult { Success = false, Message = "Der zuerst gewaehlte Stein muss ausgespielt werden." };
        }

        if (!_pieces.TryGetValue(command.PieceId, out var startEntry) || startEntry.Piece.Side != player)
        {
            return new MoveResult { Success = false, Message = "Ungueltiger Stein." };
        }

        var preMoveCapturers = GetLegalMoves(player, ignoreSelection: true)
            .Where(m => m.CapturedPieceIds.Count > 0)
            .Select(m => m.PieceId)
            .Distinct()
            .ToHashSet();

        var legalMoves = GetLegalMoves(player).Where(m => m.PieceId == command.PieceId).ToList();
        var move = legalMoves.FirstOrDefault(m => PathsEqual(m.Path, command.Path));
        if (move is null)
        {
            return new MoveResult { Success = false, Message = "Dieser Zug ist nach den aktuellen Regeln nicht erlaubt." };
        }

        var moveDurationMs = ConsumeTurnTime(player);
        LastMoveDurationMs = moveDurationMs;
        ExecuteMove(move, out var promoted);

        PenaltyMarker? penaltyMarker = null;
        if (move.CapturedPieceIds.Count == 0 && move.MissedMandatoryCapture)
        {
            penaltyMarker = ApplyMissedCapturePenalty(player, command.PieceId, preMoveCapturers);
        }

        if (!_pieces.TryGetValue(command.PieceId, out var currentEntry))
        {
            EvaluateGameOver();
            return new MoveResult
            {
                Success = true,
                Message = StatusMessage,
                AwaitingResolution = true,
                ContinuationRequired = false,
                PenaltyMarker = penaltyMarker
            };
        }

        var currentPiece = currentEntry.Piece;
        var canContinue = move.CapturedPieceIds.Count > 0
            && !promoted
            && AllowFurtherCapture(currentPiece)
            && GetCaptureMovesForPiece(command.PieceId).Count > 0;

        if (canContinue)
        {
            SelectedPieceId = command.PieceId;
            _turnStartedAt = DateTimeOffset.UtcNow;
            StatusMessage = $"{Players[player]} muss die Schlagserie fortsetzen.";
            return new MoveResult
            {
                Success = true,
                Message = StatusMessage,
                AwaitingResolution = true,
                ContinuationRequired = true,
                PenaltyMarker = penaltyMarker
            };
        }

        SelectedPieceId = null;
        UpdateBestMove(player, moveDurationMs);
        StatusMessage = "Zug abgeschlossen.";

        return new MoveResult
        {
            Success = true,
            Message = StatusMessage,
            AwaitingResolution = true,
            ContinuationRequired = false,
            PenaltyMarker = penaltyMarker
        };
    }

    public MoveResult FinalizeResolvedTurn()
    {
        EvaluateGameOver();
        if (!IsGameOver)
        {
            AdvanceTurn();
            StatusMessage = $"Am Zug: {Players[CurrentTurn]}";
        }

        return new MoveResult { Success = true, Message = StatusMessage };
    }

    public MoveResult ResolveMissedContinuation(PlayerSide player, string pieceId)
    {
        var penaltyMarker = ApplyMissedCapturePenalty(player, pieceId, [pieceId]);
        SelectedPieceId = null;
        EvaluateGameOver();
        if (!IsGameOver)
        {
            AdvanceTurn();
            StatusMessage = $"Am Zug: {Players[CurrentTurn]}";
        }

        return new MoveResult
        {
            Success = true,
            Message = StatusMessage,
            PenaltyMarker = penaltyMarker
        };
    }

    public GameSnapshot ToSnapshot(
        string roomCode,
        string hostName,
        string phase,
        Dictionary<PlayerSide, PlayerStats> stats,
        Dictionary<PlayerSide, bool> readyStates,
        Dictionary<PlayerSide, bool> drawOffers,
        Dictionary<PlayerSide, PlayerAppearance> appearanceBySide,
        DateTimeOffset? countdownEndsAt,
        DateTimeOffset? resolutionDeadlineAt,
        bool continuationRequired,
        PenaltyMarker? penaltyMarker)
    {
        var remaining = new Dictionary<PlayerSide, long>(_remainingTurnMs)
        {
            [CurrentTurn] = Math.Max(0, _remainingTurnMs[CurrentTurn] + (long)(DateTimeOffset.UtcNow - _turnStartedAt).TotalMilliseconds)
        };

        return new GameSnapshot
        {
            RoomCode = roomCode,
            HostName = hostName,
            Phase = phase,
            Rules = CloneRules(Rules),
            Pieces = _pieces.Values.ToDictionary(
                v => v.Piece.Id,
                v => new PieceView
                {
                    Id = v.Piece.Id,
                    Side = v.Piece.Side,
                    IsKing = v.Piece.IsKing,
                    Row = v.Row,
                    Col = v.Col
                }),
            CurrentTurn = CurrentTurn,
            Players = new Dictionary<PlayerSide, string>(Players),
            RemainingTurnMs = remaining,
            Stats = stats,
            ReadyStates = new Dictionary<PlayerSide, bool>(readyStates),
            DrawOffers = new Dictionary<PlayerSide, bool>(drawOffers),
            AppearanceBySide = appearanceBySide.ToDictionary(
                kvp => kvp.Key,
                kvp => new PlayerAppearance
                {
                    PieceColor = kvp.Value.PieceColor,
                    KingColor = kvp.Value.KingColor
                }),
            CountdownEndsAtUnixMs = countdownEndsAt?.ToUnixTimeMilliseconds(),
            ResolutionDeadlineUnixMs = resolutionDeadlineAt?.ToUnixTimeMilliseconds(),
            ContinuationRequired = continuationRequired,
            PenaltyMarker = penaltyMarker,
            IsGameOver = IsGameOver,
            StatusMessage = StatusMessage,
            SelectedPieceId = SelectedPieceId,
            ForcedDestinations = SelectedPieceId is null ? [] : GetForcedDestinations(CurrentTurn).ToList()
        };
    }

    public (string Winner, string Loser)? TryCommitStats(Func<string, Action<PlayerStats>, Task<PlayerProfile>> updatePlayer)
    {
        if (!IsGameOver || _statsCommitted)
        {
            return null;
        }

        _statsCommitted = true;
        return StatusMessage.Contains(Players[PlayerSide.White], StringComparison.Ordinal)
            ? (Players[PlayerSide.White], Players[PlayerSide.Black])
            : (Players[PlayerSide.Black], Players[PlayerSide.White]);
    }

    private void SetupBoard()
    {
        _pieces.Clear();
        _capturedWhite.Clear();
        _capturedBlack.Clear();

        var whiteIndex = 1;
        var blackIndex = 1;
        for (var row = 0; row < 10; row++)
        {
            for (var col = 0; col < 10; col++)
            {
                if (!IsPlayableSquare(row, col))
                {
                    continue;
                }

                if (row < 4)
                {
                    AddPiece(row, col, new Piece { Id = $"b{blackIndex++}", Side = PlayerSide.Black });
                }
                else if (row > 5)
                {
                    AddPiece(row, col, new Piece { Id = $"w{whiteIndex++}", Side = PlayerSide.White });
                }
            }
        }
    }

    private void AddPiece(int row, int col, Piece piece) => _pieces[piece.Id] = (row, col, piece);

    private static bool IsPlayableSquare(int row, int col) => (row + col) % 2 == 1;

    private static int ForwardStep(PlayerSide side) => side == PlayerSide.White ? -1 : 1;

    private List<LegalMove> GetLegalMoves(PlayerSide player, bool ignoreSelection = false)
    {
        var allMoves = _pieces.Values
            .Where(v => v.Piece.Side == player)
            .SelectMany(v => GetMovesForPiece(v.Piece.Id))
            .ToList();

        if (!ignoreSelection && SelectedPieceId is not null)
        {
            allMoves = allMoves.Where(m => m.PieceId == SelectedPieceId).ToList();
        }

        var capturingPieces = _pieces.Values
            .Where(v => v.Piece.Side == player)
            .SelectMany(v => GetMovesForPiece(v.Piece.Id))
            .Where(m => m.CapturedPieceIds.Count > 0)
            .Select(m => m.PieceId)
            .Distinct()
            .ToHashSet();

        foreach (var move in allMoves.Where(m => m.CapturedPieceIds.Count == 0))
        {
            move.MissedMandatoryCapture = capturingPieces.Count > 0;
        }

        return allMoves;
    }

    private List<LegalMove> GetMovesForPiece(string pieceId)
    {
        var captures = GetCaptureMovesForPiece(pieceId);
        var moves = new List<LegalMove>(captures);
        moves.AddRange(GetNonCaptureMovesForPiece(pieceId));
        return moves;
    }

    private List<LegalMove> GetCaptureMovesForPiece(string pieceId)
    {
        if (!_pieces.TryGetValue(pieceId, out var entry))
        {
            return [];
        }

        return entry.Piece.IsKing && Rules.KingsMoveMultipleSquares
            ? GetFlyingKingCaptures(pieceId, entry.Row, entry.Col, CloneBoard(), [], null)
            : GetStandardCaptures(pieceId, entry.Row, entry.Col, CloneBoard(), [], null);
    }

    private Dictionary<string, (int Row, int Col, Piece Piece)> CloneBoard() =>
        _pieces.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.Row, kvp.Value.Col, new Piece
            {
                Id = kvp.Value.Piece.Id,
                Side = kvp.Value.Piece.Side,
                IsKing = kvp.Value.Piece.IsKing
            }));

    private List<LegalMove> GetStandardCaptures(
        string pieceId,
        int row,
        int col,
        Dictionary<string, (int Row, int Col, Piece Piece)> board,
        List<Position> currentPath,
        (int dRow, int dCol)? lastDirection)
    {
        var piece = board[pieceId].Piece;
        var moves = new List<LegalMove>();
        var directions = GetCaptureDirections(piece, lastDirection);

        foreach (var (dRow, dCol) in directions)
        {
            var middle = (Row: row + dRow, Col: col + dCol);
            var landing = (Row: row + (2 * dRow), Col: col + (2 * dCol));
            if (!Inside(landing.Row, landing.Col) || !Inside(middle.Row, middle.Col))
            {
                continue;
            }

            if (!IsPlayableSquare(landing.Row, landing.Col))
            {
                continue;
            }

            var captured = board.Values.FirstOrDefault(v => v.Row == middle.Row && v.Col == middle.Col);
            var occupiedLanding = board.Values.Any(v => v.Row == landing.Row && v.Col == landing.Col);
            if (captured.Piece is null || captured.Piece.Side == piece.Side || occupiedLanding)
            {
                continue;
            }

            moves.Add(new LegalMove
            {
                PieceId = pieceId,
                Path =
                [
                    new() { Row = row, Col = col },
                    new() { Row = landing.Row, Col = landing.Col }
                ],
                CapturedPieceIds = [captured.Piece.Id]
            });
        }

        return moves;
    }

    private List<LegalMove> GetFlyingKingCaptures(
        string pieceId,
        int row,
        int col,
        Dictionary<string, (int Row, int Col, Piece Piece)> board,
        List<Position> currentPath,
        (int dRow, int dCol)? lastDirection)
    {
        var piece = board[pieceId].Piece;
        var moves = new List<LegalMove>();
        var directions = GetCaptureDirections(piece, lastDirection);

        foreach (var (dRow, dCol) in directions)
        {
            var enemyId = string.Empty;
            var step = 1;
            while (Inside(row + (step * dRow), col + (step * dCol)))
            {
                var targetRow = row + (step * dRow);
                var targetCol = col + (step * dCol);
                var occupant = board.Values.FirstOrDefault(v => v.Row == targetRow && v.Col == targetCol);
                if (occupant.Piece is null)
                {
                    if (!string.IsNullOrEmpty(enemyId))
                    {
                        var captured = board[enemyId];
                        var directLanding = (Row: captured.Row + dRow, Col: captured.Col + dCol);
                        if (targetRow != directLanding.Row || targetCol != directLanding.Col)
                        {
                            step++;
                            continue;
                        }

                        moves.Add(new LegalMove
                        {
                            PieceId = pieceId,
                            Path =
                            [
                                new() { Row = row, Col = col },
                                new() { Row = targetRow, Col = targetCol }
                            ],
                            CapturedPieceIds = [enemyId]
                        });
                    }

                    step++;
                    continue;
                }

                if (occupant.Piece.Side == piece.Side || !string.IsNullOrEmpty(enemyId))
                {
                    break;
                }

                enemyId = occupant.Piece.Id;
                step++;
            }
        }

        return moves;
    }

    private List<(int dRow, int dCol)> GetCaptureDirections(Piece piece, (int dRow, int dCol)? lastDirection)
    {
        List<(int dRow, int dCol)> directions;
        if (piece.IsKing || Rules.MenCanCaptureBackward)
        {
            directions =
            [
                (-1, -1), (-1, 1), (1, -1), (1, 1)
            ];
        }
        else
        {
            var forward = ForwardStep(piece.Side);
            directions =
            [
                (forward, -1), (forward, 1)
            ];
        }

        if (lastDirection.HasValue)
        {
            directions = directions.Where(d => d == lastDirection.Value).ToList();
        }

        return directions;
    }

    private List<LegalMove> GetNonCaptureMovesForPiece(string pieceId)
    {
        if (!_pieces.TryGetValue(pieceId, out var entry))
        {
            return [];
        }

        var piece = entry.Piece;
        var moves = new List<LegalMove>();
        if (piece.IsKing && Rules.KingsMoveMultipleSquares)
        {
            foreach (var (dRow, dCol) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
            {
                var step = 1;
                while (Inside(entry.Row + (step * dRow), entry.Col + (step * dCol)))
                {
                    var target = (Row: entry.Row + (step * dRow), Col: entry.Col + (step * dCol));
                    if (_pieces.Values.Any(v => v.Row == target.Row && v.Col == target.Col))
                    {
                        break;
                    }

                    moves.Add(new LegalMove
                    {
                        PieceId = pieceId,
                        Path =
                        [
                            new() { Row = entry.Row, Col = entry.Col },
                            new() { Row = target.Row, Col = target.Col }
                        ],
                        CapturedPieceIds = []
                    });
                    step++;
                }
            }

            return moves;
        }

        var directions = piece.IsKing
            ? new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) }
            : new[] { (ForwardStep(piece.Side), -1), (ForwardStep(piece.Side), 1) };

        foreach (var (dRow, dCol) in directions)
        {
            var target = (Row: entry.Row + dRow, Col: entry.Col + dCol);
            if (!Inside(target.Row, target.Col) || !IsPlayableSquare(target.Row, target.Col))
            {
                continue;
            }

            if (_pieces.Values.Any(v => v.Row == target.Row && v.Col == target.Col))
            {
                continue;
            }

            moves.Add(new LegalMove
            {
                PieceId = pieceId,
                Path =
                [
                    new() { Row = entry.Row, Col = entry.Col },
                    new() { Row = target.Row, Col = target.Col }
                ],
                CapturedPieceIds = []
            });
        }

        return moves;
    }

    private static bool Inside(int row, int col) => row >= 0 && row < 10 && col >= 0 && col < 10;

    private void ExecuteMove(LegalMove move, out bool promoted)
    {
        promoted = false;
        var current = _pieces[move.PieceId];
        var last = move.Path[^1];

        _pieces[move.PieceId] = (last.Row, last.Col, current.Piece);
        foreach (var capturedId in move.CapturedPieceIds)
        {
            if (_pieces.Remove(capturedId, out var removed))
            {
                if (removed.Piece.Side == PlayerSide.White)
                {
                    _capturedWhite.Add(removed.Piece);
                }
                else
                {
                    _capturedBlack.Add(removed.Piece);
                }
            }
        }

        if (!current.Piece.IsKing)
        {
            var promotionRow = current.Piece.Side == PlayerSide.White ? 0 : 9;
            if (last.Row == promotionRow)
            {
                current.Piece.IsKing = true;
                promoted = true;
            }
        }
    }

    private PenaltyMarker? ApplyMissedCapturePenalty(PlayerSide player, string movedPieceId, HashSet<string> preMoveCapturers)
    {
        switch (Rules.MissedCapturePenalty)
        {
            case PenaltyType.None:
                StatusMessage = $"{Players[player]} hat die Schlagpflicht ignoriert.";
                return null;
            case PenaltyType.TimePenalty:
                _remainingTurnMs[player] += Rules.MissedCaptureTimePenaltySeconds * 1000L;
                StatusMessage = $"{Players[player]} ignoriert die Schlagpflicht und erhaelt eine Zeitstrafe.";
                return null;
            case PenaltyType.RestoreOpponentPiece:
                return RestoreOpponentPiece(player);
            default:
                return RemovePenaltyPiece(player, movedPieceId, preMoveCapturers);
        }
    }

    private PenaltyMarker? RemovePenaltyPiece(PlayerSide player, string movedPieceId, HashSet<string> preMoveCapturers)
    {
        var promotionRow = player == PlayerSide.White ? 0 : 9;
        var ordered = preMoveCapturers
            .Where(id => _pieces.ContainsKey(id))
            .Select(id => new
            {
                Id = id,
                Distance = Math.Abs(_pieces[id].Row - promotionRow)
            })
            .OrderBy(entry => entry.Distance)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();

        var targetId = ordered.FirstOrDefault()?.Id;
        if (targetId is not null && _pieces.Remove(targetId, out var removed))
        {
            StatusMessage = "Schlagpflicht verletzt. Ein schlagfaehiger Stein wurde entfernt.";
            return CreatePenaltyMarker(removed.Row, removed.Col, StatusMessage);
        }

        return null;
    }

    private PenaltyMarker? RestoreOpponentPiece(PlayerSide player)
    {
        var opponent = player == PlayerSide.White ? PlayerSide.Black : PlayerSide.White;
        var capturedList = opponent == PlayerSide.White ? _capturedWhite : _capturedBlack;
        var piece = capturedList.LastOrDefault();
        if (piece is null)
        {
            return null;
        }

        foreach (var row in GetSpawnRows(opponent))
        {
            for (var col = 0; col < 10; col++)
            {
                if (!IsPlayableSquare(row, col) || _pieces.Values.Any(v => v.Row == row && v.Col == col))
                {
                    continue;
                }

                capturedList.Remove(piece);
                piece.IsKing = false;
                AddPiece(row, col, piece);
                StatusMessage = $"{Players[player]} ignoriert die Schlagpflicht. Der Gegner erhaelt einen Stein zurueck.";
                return CreatePenaltyMarker(row, col, StatusMessage);
            }
        }

        return null;
    }

    private static IEnumerable<int> GetSpawnRows(PlayerSide side)
    {
        if (side == PlayerSide.White)
        {
            for (var row = 9; row >= 0; row--)
            {
                yield return row;
            }
        }
        else
        {
            for (var row = 0; row < 10; row++)
            {
                yield return row;
            }
        }
    }

    private bool AllowFurtherCapture(Piece piece) =>
        piece.IsKing ? Rules.KingsCanMultiCapture : Rules.AllowMultiCapture;

    private void AdvanceTurn()
    {
        CurrentTurn = CurrentTurn == PlayerSide.White ? PlayerSide.Black : PlayerSide.White;
        _turnStartedAt = DateTimeOffset.UtcNow;
    }

    private long ConsumeTurnTime(PlayerSide side)
    {
        var elapsed = Math.Max(0, (long)(DateTimeOffset.UtcNow - _turnStartedAt).TotalMilliseconds);
        _remainingTurnMs[side] += elapsed;
        return elapsed;
    }

    private void UpdateBestMove(PlayerSide player, long moveDurationMs)
    {
        if (moveDurationMs < 0)
        {
            return;
        }
    }

    public long GetLastMoveDuration(PlayerSide side)
    {
        return 0;
    }

    private void EvaluateGameOver()
    {
        foreach (var side in new[] { PlayerSide.White, PlayerSide.Black })
        {
            var ownsPiece = _pieces.Values.Any(v => v.Piece.Side == side);
            if (!ownsPiece)
            {
                var winner = side == PlayerSide.White ? PlayerSide.Black : PlayerSide.White;
                DeclareWinner(winner, $"{Players[winner]} gewinnt, weil keine Steine mehr uebrig sind.");
                return;
            }

            var legalMoves = GetLegalMoves(side);
            if (legalMoves.Count == 0)
            {
                var winner = side == PlayerSide.White ? PlayerSide.Black : PlayerSide.White;
                DeclareWinner(winner, $"{Players[winner]} gewinnt, weil kein Zug mehr moeglich ist.");
                return;
            }
        }
    }

    private void DeclareWinner(PlayerSide winner, string message)
    {
        IsGameOver = true;
        StatusMessage = message;
        SelectedPieceId = null;
    }

    private static bool PathsEqual(List<Position> left, List<Position> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].Row != right[i].Row || left[i].Col != right[i].Col)
            {
                return false;
            }
        }

        return true;
    }

    private static Piece ClonePiece(Piece piece) =>
        new()
        {
            Id = piece.Id,
            Side = piece.Side,
            IsKing = piece.IsKing
        };

    private static Dictionary<string, (int Row, int Col, Piece Piece)> CloneBoard(Dictionary<string, (int Row, int Col, Piece Piece)> source) =>
        source.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.Row, kvp.Value.Col, ClonePiece(kvp.Value.Piece)));

    private static RulesConfig CloneRules(RulesConfig rules) =>
        new()
        {
            MandatoryCapture = rules.MandatoryCapture,
            AllowMultiCapture = rules.AllowMultiCapture,
            RequireMultiCapture = rules.RequireMultiCapture,
            MenCanCaptureBackward = rules.MenCanCaptureBackward,
            KingsMoveMultipleSquares = rules.KingsMoveMultipleSquares,
            KingsMustLandDirectlyAfterCapture = rules.KingsMustLandDirectlyAfterCapture,
            KingsMustCapture = rules.KingsMustCapture,
            KingsCanMultiCapture = rules.KingsCanMultiCapture,
            KingsCanChangeDirectionDuringMultiCapture = rules.KingsCanChangeDirectionDuringMultiCapture,
            MissedCapturePenalty = rules.MissedCapturePenalty,
            MissedCaptureTimePenaltySeconds = rules.MissedCaptureTimePenaltySeconds
        };

    private static PenaltyMarker CreatePenaltyMarker(int row, int col, string message) =>
        new()
        {
            Row = row,
            Col = col,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds(),
            Message = message
        };


    private sealed class LegalMove
    {
        public required string PieceId { get; init; }
        public required List<Position> Path { get; init; }
        public required List<string> CapturedPieceIds { get; init; }
        public bool MissedMandatoryCapture { get; set; }
    }
}
