namespace RimForge.Steam.Assistant;

public enum PackSize { Small, Normal, Large }

/// <summary>What the player asked the assistant for, in a form the planner understands.</summary>
public sealed record PackRequest
{
    /// <summary>Theme ids from <see cref="PackTopics"/>, in the order the player named them.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>Extra Workshop searches (English words: "warhammer", "star wars"…).</summary>
    public IReadOnlyList<string> Words { get; init; } = [];

    /// <summary>Themes the player does not want ("без магии").</summary>
    public IReadOnlyList<string> ExcludeTopics { get; init; } = [];

    /// <summary>Words that must not be in a mod's name ("no Combat Extended").</summary>
    public IReadOnlyList<string> ExcludeWords { get; init; } = [];

    /// <summary>A fast game is wanted: fewer and lighter mods, plus performance mods.</summary>
    public bool Fps { get; init; }

    /// <summary>A stable game is wanted: only popular, maintained mods.</summary>
    public bool Stable { get; init; }

    public PackSize Size { get; init; } = PackSize.Normal;

    /// <summary>An exact number of mods, if the player gave one.</summary>
    public int? Count { get; init; }

    /// <summary>Only mods that work without the expansions.</summary>
    public bool NoDlc { get; init; }

    /// <summary>No theme was named ("сборка для слабого ПК"): the themes are RimForge's guess.</summary>
    public bool GuessedTopics { get; init; }

    public bool IsEmpty => Topics.Count == 0 && Words.Count == 0;

    /// <summary>How many mods the pack aims at (performance mods come on top).</summary>
    public int Target => Count ?? (Size, Fps) switch
    {
        (PackSize.Small, _) => 15,
        (PackSize.Large, false) => 60,
        (PackSize.Large, true) => 40,
        (_, true) => 25,
        _ => 30,
    };
}
