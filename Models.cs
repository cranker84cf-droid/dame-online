using System.Text.Json;
using System.Text.Json.Serialization;

namespace CheckersOnline;

public enum PlayerSide
{
    White,
    Black
}

public enum PenaltyType
{
    None,
    RemoveCapturingPiece,
    TimePenalty,
    RestoreOpponentPiece
}

public sealed class RulesConfig
{
    public bool MandatoryCapture { get; set; } = true;
    public bool AllowMultiCapture { get; set; } = true;
    public bool RequireMultiCapture { get; set; } = true;
    public bool MenCanCaptureBackward { get; set; } = false;
    public bool KingsMoveMultipleSquares { get; set; } = true;
    public bool KingsMustCapture { get; set; } = true;
    public bool KingsCanMultiCapture { get; set; } = true;
    public bool KingsCanChangeDirectionDuringMultiCapture { get; set; } = true;
    public PenaltyType MissedCapturePenalty { get; set; } = PenaltyType.RemoveCapturingPiece;
    public int MissedCaptureTimePenaltySeconds { get; set; } = 10;
}

public sealed class Piece
{
    public required string Id { get; init; }
    public required PlayerSide Side { get; init; }
    public bool IsKing { get; set; }
}

public sealed class Position
{
    public int Row { get; init; }
    public int Col { get; init; }
}

public sealed class MoveCommand
{
    public required string PieceId { get; init; }
    public required List<Position> Path { get; init; }
}

public sealed class MoveResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
}

public sealed class PlayerStats
{
    public int TotalGames { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public long? BestMoveTimeMs { get; set; }
}

public sealed class PlayerProfile
{
    public required string Name { get; init; }
    public required PlayerStats Stats { get; init; }
}

public sealed class PlayerSession
{
    public required string ConnectionId { get; init; }
    public required string Name { get; init; }
    public required string RoomCode { get; init; }
    public required PlayerSide Side { get; init; }
}

public sealed class GameSnapshot
{
    public required string RoomCode { get; init; }
    public required string HostName { get; init; }
    public required RulesConfig Rules { get; init; }
    public required Dictionary<string, PieceView> Pieces { get; init; }
    public required PlayerSide CurrentTurn { get; init; }
    public required Dictionary<PlayerSide, string> Players { get; init; }
    public required Dictionary<PlayerSide, long> RemainingTurnMs { get; init; }
    public required Dictionary<PlayerSide, PlayerStats> Stats { get; init; }
    public required bool IsGameOver { get; init; }
    public required string StatusMessage { get; init; }
    public required string? SelectedPieceId { get; init; }
    public required List<Position> ForcedDestinations { get; init; }
}

public sealed class PieceView
{
    public required string Id { get; init; }
    public required PlayerSide Side { get; init; }
    public required bool IsKing { get; init; }
    public required int Row { get; init; }
    public required int Col { get; init; }
}

public sealed class ClientEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }
}

public sealed class CreateRoomRequest
{
    public required string Name { get; init; }
}

public sealed class JoinRoomRequest
{
    public required string Name { get; init; }
    public required string RoomCode { get; init; }
}

public sealed class SelectPieceRequest
{
    public required string PieceId { get; init; }
}

public sealed class UpdateRulesRequest
{
    public required RulesConfig Rules { get; init; }
}

public sealed class SetColorRequest
{
    public required string PieceColor { get; init; }
    public required string KingColor { get; init; }
}
