namespace AiNative.BattleHost;

internal enum BattleGameModeKind : byte
{
    Acceptance = 0,
    Arena = 1,
}

internal sealed record BattleGameModeSettings(BattleGameModeKind Kind)
{
    public bool IsArena => Kind == BattleGameModeKind.Arena;

    public int GetConnectionCapacity(BattleHostCapacitySettings capacitySettings)
        => IsArena ? ArenaRoom.MaxPlayers : capacitySettings.TotalBotCapacity;

    public static BattleGameModeSettings Create(IConfiguration configuration)
    {
        string mode = configuration.GetValue("AINATIVE_GAME_MODE", "acceptance");
        return mode.ToLowerInvariant() switch
        {
            "acceptance" => new BattleGameModeSettings(BattleGameModeKind.Acceptance),
            "arena" => new BattleGameModeSettings(BattleGameModeKind.Arena),
            _ => throw new InvalidOperationException(
                "AINATIVE_GAME_MODE must be either 'acceptance' or 'arena'."),
        };
    }
}
