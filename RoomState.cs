namespace CheckersOnline;

public sealed class RoomState
{
    public required string Code { get; init; }
    public required string HostConnectionId { get; set; }
    public required string HostName { get; set; }
    public required RulesConfig Rules { get; set; }
    public required Dictionary<string, PlayerSession> Sessions { get; init; }
    public required GameState Game { get; set; }
    public required Dictionary<PlayerSide, bool> ReadyBySide { get; init; }
    public required Dictionary<PlayerSide, bool> DrawOfferBySide { get; init; }
    public required Dictionary<PlayerSide, PlayerAppearance> AppearanceBySide { get; init; }
    public required string Phase { get; set; }
    public DateTimeOffset? CountdownEndsAt { get; set; }
    public DateTimeOffset? ResolutionDeadlineAt { get; set; }
    public bool ContinuationRequired { get; set; }
    public PenaltyMarker? PenaltyMarker { get; set; }
    public CancellationTokenSource? PendingResolutionCts { get; set; }
}
