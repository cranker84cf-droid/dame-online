using System.Text.Json;
using CheckersOnline;

namespace CheckersOnline.Services;

public sealed class PlayerStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, PlayerProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public PlayerStore(IWebHostEnvironment env)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "players.json");
        if (File.Exists(_filePath))
        {
            var text = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, PlayerProfile>>(text, _jsonOptions);
            if (parsed is not null)
            {
                _profiles = parsed;
            }
        }
    }

    public async Task<PlayerProfile> GetOrCreateAsync(string name)
    {
        var normalized = Normalize(name);
        await _gate.WaitAsync();
        try
        {
            if (_profiles.TryGetValue(normalized, out var existing))
            {
                return Clone(existing);
            }

            var created = new PlayerProfile
            {
                Name = name.Trim(),
                Stats = new PlayerStats()
            };
            _profiles[normalized] = Clone(created);
            await SaveAsync();
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayerProfile> UpdateAsync(string name, Action<PlayerStats> update)
    {
        var normalized = Normalize(name);
        await _gate.WaitAsync();
        try
        {
            if (!_profiles.TryGetValue(normalized, out var existing))
            {
                existing = new PlayerProfile
                {
                    Name = name.Trim(),
                    Stats = new PlayerStats()
                };
                _profiles[normalized] = existing;
            }

            update(existing.Stats);
            await SaveAsync();
            return Clone(existing);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_profiles, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static PlayerProfile Clone(PlayerProfile profile) =>
        new()
        {
            Name = profile.Name,
            Stats = new PlayerStats
            {
                TotalGames = profile.Stats.TotalGames,
                Wins = profile.Stats.Wins,
                Losses = profile.Stats.Losses,
                BestMoveTimeMs = profile.Stats.BestMoveTimeMs
            }
        };
}
