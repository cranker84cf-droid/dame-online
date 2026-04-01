namespace CheckersOnline;

public sealed class RoomState
{
    public required string Code { get; init; }
    public required string HostConnectionId { get; set; }
    public required string HostName { get; set; }
    public required RulesConfig Rules { get; set; }
    public required Dictionary<string, PlayerSession> Sessions { get; init; }
    public required GameState Game { get; set; }
}
