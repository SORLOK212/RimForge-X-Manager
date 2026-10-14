using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimForge.Core.Checks;
using RimForge.Core.Content;
using RimForge.Core.Diagnostics;
using RimForge.Core.Models;
using RimForge.Core.Sorting;
using RimForge.Core.Storage;
using RimForge.Steam.Workshop;

namespace RimForge.Mcp;

public sealed record ToolResult(string Text, bool IsError = false);

/// <summary>One tool an AI assistant can call.</summary>
public sealed record ToolInfo(string Name, string Title, string Description, string InputSchema, bool Changes)
{
    public JsonObject ToJson()
    {
        var tool = new JsonObject
        {
            ["name"] = Name,
            ["title"] = Title,
            ["description"] = Description,
            ["inputSchema"] = JsonNode.Parse(InputSchema),
            ["annotations"] = new JsonObject
            {
                ["title"] = Title,
                ["readOnlyHint"] = !Changes,
                ["destructiveHint"] = Changes,
                ["openWorldHint"] = Name is "search_workshop" or WriteActions.Download,
            },
        };
        return tool;
    }
}

/// <summary>
/// The RimForge tools for AI assistants (Claude, Codex, Cursor, Gemini, Copilot… — any MCP client).
/// Reading works from the files; changes go through the RimForge window, which asks the player first.
/// </summary>
public sealed class RimForgeTools(IRimForgeHost host)
{
    private const string NoArgs = """{"type":"object","properties":{}}""";
    private static readonly string Categories = string.Join(", ", Enum.GetNames<ModCategory>().Select(c => c.ToLowerInvariant()));

    public static IReadOnlyList<ToolInfo> All { get; } =
    [
        new("get_overview", "RimForge overview",
            "Start here. The game version, the active modpack, how many mods are installed and active, the check summary and what the AI may change.",
            NoArgs, false),
        new("list_mods", "List mods",
            "Lists mods with their package ids (the ids every other tool uses). state: 'active' (load order, default), 'inactive' (installed but off) or 'all'. " +
            $"Optional search (name, author or id) and category ({Categories}).",
            """{"type":"object","properties":{"state":{"type":"string","enum":["active","inactive","all"]},"search":{"type":"string"},"category":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":1000},"offset":{"type":"integer","minimum":0}}}""",
            false),
        new("get_mod", "Mod details",
            "Everything about one mod: description, supported game versions, what it needs, what it contains, its problems and why it sits where it does in the load order.",
            """{"type":"object","properties":{"id":{"type":"string","description":"Package id (e.g. brrainz.harmony) or Workshop id"}},"required":["id"]}""",
            false),
        new("check_modlist", "Check the mod list",
            "The traffic light: missing or disabled dependencies, incompatible mods, wrong order, unsupported game versions, duplicates, missing DLC and abandoned mods with maintained replacements.",
            NoArgs, false),
        new("preview_sort", "Preview sorting",
            "Shows what sorting would move and why, without changing anything. mode: 'careful' (fewest moves, default) or 'full' (rebuild by groups).",
            """{"type":"object","properties":{"mode":{"type":"string","enum":["careful","full"]}}}""", false),
        new("list_modpacks", "List modpacks",
            "The player's modpacks (saved mod lists) and which one is active.", NoArgs, false),
        new("search_workshop", "Search the Steam Workshop",
            "Searches RimWorld mods on the Steam Workshop. Returns Workshop ids for download_mods, subscribers, game versions and whether the mod is already installed. " +
            "sort: 'relevance' (default with text), 'popular' (this week), 'subscribers', 'recent', 'updated'. 30 results per page.",
            """{"type":"object","properties":{"query":{"type":"string"},"sort":{"type":"string","enum":["relevance","popular","subscribers","recent","updated"]},"only_my_version":{"type":"boolean","description":"Only mods tagged for the current game version (default true)"},"page":{"type":"integer","minimum":1}}}""",
            false),
        new("analyze_log", "Analyze the game log",
            "Reads the log of the last game run (Player.log), groups the errors and names the mods most likely behind each one. previous=true reads the run before that. Can take a while on big modpacks.",
            """{"type":"object","properties":{"previous":{"type":"boolean"}}}""", false),

        new(WriteActions.Enable, "Turn mods on",
            "Turns installed mods on and puts them in a correct place (careful sort). Dependencies are not added automatically: check them with get_mod or check_modlist.",
            """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"minItems":1}},"required":["ids"]}""", true),
        new(WriteActions.Disable, "Turn mods off",
            "Turns mods off (they stay installed). RimForge warns the player if a save uses them.",
            """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"minItems":1}},"required":["ids"]}""", true),
        new(WriteActions.SetLoadOrder, "Set the whole load order",
            "Replaces the list of active mods with exactly these ids, in this order. Must start with the game (ludeon.rimworld) and its DLC; every id must be installed.",
            """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"minItems":1}},"required":["ids"]}""", true),
        new(WriteActions.Sort, "Sort the mods",
            "Sorts the active mods by their rules. mode: 'careful' (fewest moves, default) or 'full'.",
            """{"type":"object","properties":{"mode":{"type":"string","enum":["careful","full"]}}}""", true),
        new(WriteActions.AddRule, "Add an order rule",
            "Saves the player's own rule in the modpack: 'before' always loads above 'after'. Player rules win over the authors' rules. The list is re-sorted.",
            """{"type":"object","properties":{"before":{"type":"string"},"after":{"type":"string"}},"required":["before","after"]}""", true),
        new(WriteActions.Pin, "Pin a mod",
            "Pins a mod to the top (right after the game and DLC) or the bottom of the list; 'none' removes the pin.",
            """{"type":"object","properties":{"id":{"type":"string"},"place":{"type":"string","enum":["top","bottom","none"]}},"required":["id","place"]}""", true),
        new(WriteActions.CreateModpack, "Create a modpack",
            "Creates a new modpack. ids: its mods in order (installed package ids, or 'workshop:<id>' for mods to download); without ids it copies the current list. " +
            "switch=true (default) makes it active; mods given as workshop:<id> are downloaded and turned on.",
            """{"type":"object","properties":{"name":{"type":"string"},"ids":{"type":"array","items":{"type":"string"}},"switch":{"type":"boolean"}},"required":["name"]}""", true),
        new(WriteActions.SwitchModpack, "Switch modpack",
            "Makes another modpack active (by name or id) and writes its mod list to the game.",
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""", true),
        new(WriteActions.Download, "Download mods from the Workshop",
            "Subscribes to Workshop mods (ids from search_workshop) on the player's Steam account; Steam downloads them. enable=true (default) turns them on in the active modpack when done. " +
            "Their dependencies are downloaded too. Downloads continue in the background.",
            """{"type":"object","properties":{"workshop_ids":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":200},"enable":{"type":"boolean"}},"required":["workshop_ids"]}""", true),
        new(WriteActions.Launch, "Launch RimWorld",
            "Saves the mod list and starts the game.", NoArgs, true),
    ];

    private static readonly Lazy<WorkshopWeb> Web = new(() => new WorkshopWeb(WorkshopWeb.CreateHttpClient()));
    private static (int Hash, ModIndex Index)? _index;

    public async Task<ToolResult> CallAsync(string name, JsonElement args, string? client, CancellationToken ct)
    {
        var info = All.FirstOrDefault(t => t.Name == name);
        if (info is null) return new ToolResult($"Unknown tool '{name}'.", true);
        try
        {
            var state = await host.GetStateAsync(ct);
            if (info.Changes && state.Access == AiAccessModes.Read)
                return new ToolResult("RimForge lets AI assistants only look, not change anything. The player can allow changes in RimForge → Settings → AI assistant.", true);

            return name switch
            {
                "get_overview" => Overview(state),
                "list_mods" => ListMods(state, args),
                "get_mod" => GetMod(state, args),
                "check_modlist" => Check(state),
                "preview_sort" => PreviewSort(state, args),
                "list_modpacks" => ListModpacks(state),
                "search_workshop" => await SearchWorkshopAsync(state, args, ct),
                "analyze_log" => await AnalyzeLogAsync(state, args, ct),
                _ => await WriteAsync(state, name, args, client, ct),
            };
        }
        catch (ArgumentException ex)
        {
            return new ToolResult(ex.Message, true);
        }
        catch (InvalidOperationException ex)
        {
            return new ToolResult(ex.Message, true);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult("Network error: " + ex.Message, true);
        }
    }

    // ───────────────────────── Reading ─────────────────────────

    private static ToolResult Overview(RimForgeState s)
    {
        var errors = s.Issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = s.Issues.Count(i => i.Severity == IssueSeverity.Warning);
        var hints = s.Issues.Count(i => i.Severity == IssueSeverity.Info);
        var store = s.Paths.Store switch { Core.Paths.GameStore.Steam => "Steam", Core.Paths.GameStore.Gog => "GOG", _ => "other" };
        var sb = new StringBuilder();
        sb.AppendLine("RimForge — RimWorld mod manager.");
        sb.AppendLine(s.GameCopy is { } copy
            ? $"Game: RimWorld {s.FullGameVersion} — a saved copy from the RimForge library (frozen mods, own saves)."
            : $"Game: RimWorld {s.FullGameVersion} ({store}).");
        sb.AppendLine(s.ActiveProfile is { } p
            ? $"Active modpack: \"{p.Name}\" ({s.Profiles.Count} modpacks in total)."
            : "No modpack is active.");
        sb.AppendLine($"Mods: {s.Installed.Count} installed, {s.Active.Count} active.");
        if (s.HasUnsavedChanges) sb.AppendLine("The RimForge window has unsaved changes; you see the list as it is on screen.");
        sb.AppendLine($"Check: {errors} errors, {warnings} warnings, {hints} hints" + (errors + warnings + hints > 0 ? " — see check_modlist." : "."));
        sb.AppendLine("Expansions: " + (string.Join(", ", s.Installed.Values.Where(m => m.IsOfficial && !m.IsCore).Select(m => m.Name)) is { Length: > 0 } dlc ? dlc : "none"));
        sb.AppendLine("AI access: " + s.Access switch
        {
            AiAccessModes.Read => "read only — you can look but not change anything.",
            AiAccessModes.Full => "full — changes apply at once (the player can undo them with Ctrl+Z).",
            _ => "ask — each change waits for the player's OK in the RimForge window.",
        });
        sb.Append("Mods are named by package id (e.g. brrainz.harmony); list_mods shows them.");
        return new ToolResult(sb.ToString());
    }

    private static ToolResult ListMods(RimForgeState s, JsonElement args)
    {
        var state = Str(args, "state") ?? "active";
        var search = Str(args, "search")?.Trim().ToLowerInvariant();
        var category = Str(args, "category")?.Trim();
        ModCategory? cat = null;
        if (category is { Length: > 0 })
            cat = Enum.TryParse<ModCategory>(category, true, out var c) ? c : throw new ArgumentException($"Unknown category '{category}'. Use one of: {Categories}.");
        var limit = Int(args, "limit") ?? 300;
        var offset = Int(args, "offset") ?? 0;

        var position = new Dictionary<string, int>();
        for (var i = 0; i < s.Active.Count; i++) position.TryAdd(s.Active[i], i + 1);
        var ids = state switch
        {
            "inactive" => s.Installed.Keys.Where(id => !position.ContainsKey(id)).OrderBy(id => s.NameOf(id), StringComparer.OrdinalIgnoreCase).ToList(),
            "all" => s.Active.Concat(s.Installed.Keys.Where(id => !position.ContainsKey(id)).OrderBy(id => s.NameOf(id), StringComparer.OrdinalIgnoreCase)).ToList(),
            _ => s.Active.ToList(),
        };
        var issueMods = s.Issues.Where(i => i.Severity != IssueSeverity.Info).Select(i => i.ModId).ToHashSet();
        var rows = ids.Where(id =>
            {
                s.Installed.TryGetValue(id, out var m);
                if (search is { Length: > 0 } && !($"{s.NameOf(id)} {id} {string.Join(' ', m?.Authors ?? [])}".ToLowerInvariant().Contains(search))) return false;
                return cat is null || (m is not null && s.Content(m)?.Categories.Contains(cat.Value) == true);
            })
            .ToList();

        var sb = new StringBuilder();
        var title = state switch { "inactive" => "Installed mods that are off", "all" => "All mods (active first, in load order)", _ => "Active mods in load order" };
        sb.AppendLine($"{title}: {rows.Count}" + (rows.Count > limit || offset > 0 ? $", showing {offset + 1}–{Math.Min(rows.Count, offset + limit)}" : "") + ".");
        sb.AppendLine("Format: position. name [package id] source · game versions · categories · ⚠ = has a problem");
        foreach (var id in rows.Skip(offset).Take(limit))
        {
            s.Installed.TryGetValue(id, out var m);
            var line = new StringBuilder();
            line.Append(position.TryGetValue(id, out var pos) ? $"{pos}. " : "- ");
            line.Append(s.NameOf(id)).Append($" [{id}]");
            if (m is null) line.Append(" NOT INSTALLED");
            else
            {
                line.Append(m.Source switch { ModSource.Official => " official", ModSource.Local => " local", _ => $" workshop {m.WorkshopId}" });
                if (m.SupportedVersions.Count > 0 && !m.IsOfficial) line.Append(" · ").Append(string.Join("/", m.SupportedVersions));
                if (s.Content(m)?.Categories is { Count: > 0 } cats) line.Append(" · ").Append(string.Join(", ", cats.Select(x => x.ToString().ToLowerInvariant())));
            }
            if (issueMods.Contains(id)) line.Append(" ⚠");
            sb.AppendLine(line.ToString());
        }
        if (offset + limit < rows.Count) sb.AppendLine($"… {rows.Count - offset - limit} more (use offset={offset + limit}).");
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private static ToolResult GetMod(RimForgeState s, JsonElement args)
    {
        var query = Str(args, "id") ?? throw new ArgumentException("Give the mod's package id.");
        var mod = s.Find(query) ?? throw new ArgumentException($"No installed mod '{query}'. Use list_mods or search_workshop.");
        var id = mod.PackageId;
        var position = s.Active.ToList().IndexOf(id);
        var sb = new StringBuilder();
        sb.AppendLine($"{s.NameOf(id)} [{id}]");
        if (mod.Authors.Count > 0) sb.AppendLine("Authors: " + string.Join(", ", mod.Authors));
        sb.AppendLine("Source: " + mod.Source switch { ModSource.Official => "the game / official DLC", ModSource.Local => "local mod folder", _ => "Steam Workshop" }
            + (mod.WorkshopId is { } w ? $" (Workshop id {w}, {WorkshopIds.PageUrl(w)})" : ""));
        sb.AppendLine("State: " + (position >= 0 ? $"active, position {position + 1} of {s.Active.Count}" : "installed, turned off"));
        sb.AppendLine("Game versions: " + (mod.SupportedVersions.Count == 0 ? "any" : string.Join(", ", mod.SupportedVersions))
            + (s.GameVersion is { } v && !mod.IsOfficial && !mod.SupportsVersion(v) ? $" (current game is {v})" : ""));
        if (mod.ModVersion is { } mv) sb.AppendLine("Mod version: " + mv);

        if (mod.Dependencies.Count > 0)
        {
            sb.AppendLine("Needs:");
            foreach (var dep in mod.Dependencies)
            {
                var status = s.Active.Contains(dep.PackageId) || s.Active.Contains(dep.PackageId + "_steam") ? "active"
                    : s.Installed.ContainsKey(dep.PackageId) || s.Installed.ContainsKey(dep.PackageId + "_steam") ? "installed but OFF"
                    : "NOT INSTALLED" + (WorkshopIds.Parse(dep.SteamWorkshopUrl) is { } dw ? $" (Workshop id {dw})" : "");
                sb.AppendLine($"  - {dep.DisplayName ?? dep.PackageId} [{dep.PackageId}]: {status}");
            }
        }
        if (mod.IncompatibleWith.Count > 0) sb.AppendLine("Incompatible with: " + string.Join(", ", mod.IncompatibleWith));
        if (mod.LoadAfter.Count + mod.ForceLoadAfter.Count > 0) sb.AppendLine("Author says load after: " + string.Join(", ", mod.LoadAfter.Concat(mod.ForceLoadAfter)));
        if (mod.LoadBefore.Count + mod.ForceLoadBefore.Count > 0) sb.AppendLine("Author says load before: " + string.Join(", ", mod.LoadBefore.Concat(mod.ForceLoadBefore)));

        if (s.Content(mod) is { } p)
        {
            var parts = new (string Name, int Count)[]
            {
                ("weapons", p.Weapons), ("apparel", p.Apparel), ("races", p.Races), ("xenotypes", p.Xenotypes), ("genes", p.Genes),
                ("animals", p.Animals), ("mechanoids", p.Mechanoids), ("buildings", p.Buildings), ("plants", p.Plants), ("food", p.Food),
                ("medical", p.Medical), ("research", p.Research), ("factions", p.Factions), ("events", p.Events), ("vehicles", p.Vehicles),
                ("xml patches", p.Patches), ("code libraries (DLL)", p.Assemblies),
            }.Where(x => x.Count > 0).Select(x => $"{x.Name}: {x.Count}");
            sb.AppendLine("Contains: " + (string.Join(", ", parts) is { Length: > 0 } c ? c : "no defs") + $" · size {p.SizeBytes / 1024 / 1024} MB");
            if (p.Categories.Count > 0) sb.AppendLine("Categories: " + string.Join(", ", p.Categories.Select(x => x.ToString().ToLowerInvariant())));
        }

        var issues = s.Issues.Where(i => i.ModId == id || i.OtherId == id).ToList();
        if (issues.Count > 0)
        {
            sb.AppendLine("Problems:");
            foreach (var i in issues) sb.AppendLine("  - " + IssueLine(s, i));
        }

        if (position >= 0)
        {
            var reasons = ModSorter.Explain(id, s.Rules).Where(r => r.Rule.Source != RuleSource.Official).Take(20).ToList();
            if (reasons.Count > 0)
            {
                sb.AppendLine("Why it sits here:");
                foreach (var r in reasons)
                {
                    var other = r.OtherIsAbove ? r.Rule.Before : r.Rule.After;
                    sb.AppendLine($"  - must be {(r.OtherIsAbove ? "below" : "above")} {s.NameOf(other)} [{other}] — {RuleText(s, r.Rule)}");
                }
            }
            if (s.Rules.PinnedByUser.Contains(id)) sb.AppendLine($"  - pinned by the player to the {(s.Rules.Top.Contains(id) ? "top" : "bottom")}");
        }

        var description = BbCode.ToPlainText(mod.Description);
        if (description.Length > 0)
            sb.AppendLine("Description:").AppendLine(description.Length > 2500 ? description[..2500] + " …" : description);
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private static ToolResult Check(RimForgeState s)
    {
        if (s.Issues.Count == 0) return new ToolResult("All good: no problems found in the active mod list.");
        var sb = new StringBuilder();
        foreach (var group in s.Issues.GroupBy(i => i.Severity).OrderByDescending(g => g.Key))
        {
            sb.AppendLine(group.Key switch { IssueSeverity.Error => "ERRORS", IssueSeverity.Warning => "WARNINGS", _ => "HINTS" } + $" ({group.Count()}):");
            foreach (var i in group) sb.AppendLine("- " + IssueLine(s, i));
        }
        sb.Append("Fixes: enable_mods / download_mods for missing dependencies, sort_mods for the order, disable_mods for conflicts.");
        return new ToolResult(sb.ToString());
    }

    internal static string IssueLine(RimForgeState s, ModIssue i)
    {
        var mod = $"{s.NameOf(i.ModId)} [{i.ModId}]";
        var other = i.OtherId is { } o ? $"{(s.Installed.ContainsKey(o) ? s.NameOf(o) : i.OtherName ?? o)} [{o}]" : i.OtherName ?? "";
        var text = i.Kind switch
        {
            IssueKind.DependencyInactive => $"{mod} needs {other}, which is installed but turned off.",
            IssueKind.DependencyMissing => $"{mod} needs {other}, which is not installed" + (WorkshopIds.Parse(i.Detail) is { } w ? $" (Workshop id {w})." : "."),
            IssueKind.Incompatible => $"{mod} is incompatible with {other}" + (i.Detail is { } d ? $": {d}" : "."),
            IssueKind.UnsupportedVersion => $"{mod} does not list game version {s.GameVersion} (supports {i.Detail}); it may or may not work.",
            IssueKind.ShouldLoadAfter => $"{mod} must load after {other} but sits above it.",
            IssueKind.ShouldLoadBefore => $"{mod} must load before {other} but sits below it.",
            IssueKind.NotInstalled => $"{mod} is in the list but not installed.",
            IssueKind.BrokenAbout => $"{mod}: its About.xml has a problem: {i.Detail}",
            IssueKind.Contradiction => $"{mod} and {other}: their order rules contradict each other (a mistake in a mod's description); leave them as they are.",
            IssueKind.DuplicateActive => $"{mod} is on twice (a local copy and the Workshop copy {other}); keep one.",
            IssueKind.DuplicateInstalled => $"{mod} is installed in several folders ({i.Detail}); the game loads only one.",
            IssueKind.DlcMissing => $"{mod} needs the expansion {other}, which is not installed.",
            IssueKind.OlderVersionLikelyOk => $"{mod} was made for {i.Detail} but has no code of its own, so it most likely works on {s.GameVersion}.",
            IssueKind.UseInstead when i.Replacement is { } r => $"{mod} is abandoned; the maintained version is \"{r.NewName}\" by {r.NewAuthor ?? "?"} (Workshop id {r.NewWorkshopId}" +
                (r.NewPackageId is { } np ? $", package id {np}" : "") + ").",
            _ => $"{mod}: {i.Kind}",
        };
        if (i.Rule is { } rule) text += $" Reason: {RuleText(s, rule)}.";
        return text;
    }

    private static string RuleText(RimForgeState s, OrderRule rule) => rule.Source switch
    {
        RuleSource.Dependency => $"{s.NameOf(rule.After)} needs {s.NameOf(rule.Before)}",
        RuleSource.LoadAfter or RuleSource.LoadBefore => $"the author of {s.NameOf(rule.DeclaredBy)} says so",
        RuleSource.User => "the player's own rule",
        RuleSource.Community when rule.Comment is { } c => $"community rule: {c}",
        RuleSource.Community => "community rule",
        _ => "regular mods load after the game and DLC",
    };

    private static ToolResult PreviewSort(RimForgeState s, JsonElement args)
    {
        var full = Str(args, "mode") == "full";
        var result = full ? ModSorter.SortFull(s.Active, s.Rules, s.Installed, id => s.Installed.TryGetValue(id, out var m) ? s.Content(m) : null) : ModSorter.Sort(s.Active, s.Rules);
        var sb = new StringBuilder();
        if (!result.Changed) sb.AppendLine("Sorting would not move anything: the order already follows every rule.");
        else
        {
            sb.AppendLine($"{(full ? "Full" : "Careful")} sort would move {result.Moves.Count} mods:");
            foreach (var m in result.Moves.OrderBy(m => m.NewIndex).Take(80))
                sb.AppendLine($"- {s.NameOf(m.Id)} [{m.Id}]: {m.OldIndex + 1} → {m.NewIndex + 1}" + (m.Reason is { } r ? $" ({RuleText(s, r)})" : ""));
            if (result.Moves.Count > 80) sb.AppendLine($"… and {result.Moves.Count - 80} more.");
        }
        foreach (var c in result.Conflicts)
            sb.AppendLine($"Contradicting rules (left as they are): {string.Join(", ", c.Mods.Select(s.NameOf))}.");
        sb.Append("Nothing was changed. Call sort_mods to apply.");
        return new ToolResult(sb.ToString());
    }

    private static ToolResult ListModpacks(RimForgeState s)
    {
        if (s.Profiles.Count == 0) return new ToolResult("There are no modpacks yet.");
        var sb = new StringBuilder("Modpacks:\n");
        foreach (var p in s.Profiles)
        {
            var missing = p.Mods.Count(m => !s.Installed.ContainsKey(m.Id) && !s.Installed.ContainsKey(m.Id + "_steam"));
            sb.Append($"- \"{p.Name}\" (id {p.Id}){(p.Id == s.ActiveProfileId ? " — ACTIVE" : "")}: {p.Mods.Count} mods");
            if (missing > 0) sb.Append($", {missing} not installed");
            if (p.Rules.Count > 0) sb.Append($", {p.Rules.Count} player rules");
            if (p.Notes is { Length: > 0 } notes) sb.Append($" · notes: {(notes.Length > 120 ? notes[..120] + "…" : notes)}");
            sb.AppendLine();
        }
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private static async Task<ToolResult> SearchWorkshopAsync(RimForgeState s, JsonElement args, CancellationToken ct)
    {
        var text = Str(args, "query");
        var sort = Str(args, "sort") switch
        {
            "popular" => WorkshopSort.Trend,
            "subscribers" => WorkshopSort.MostSubscribed,
            "recent" => WorkshopSort.Newest,
            "updated" => WorkshopSort.Updated,
            "relevance" => WorkshopSort.Relevance,
            _ => string.IsNullOrWhiteSpace(text) ? WorkshopSort.Trend : WorkshopSort.Relevance,
        };
        var onlyMine = Bool(args, "only_my_version") ?? true;
        var page = await Web.Value.SearchAsync(new WorkshopQuery(text, sort, onlyMine ? s.GameVersion : null, Int(args, "page") ?? 1), ct);
        if (page.Items.Count == 0) return new ToolResult("Nothing found.");

        var installedIds = s.Installed.Values.Where(m => m.WorkshopId is not null).ToDictionary(m => m.WorkshopId!.Value, m => m.PackageId);
        var sb = new StringBuilder($"Workshop results{(text is { Length: > 0 } ? $" for \"{text}\"" : "")} (page {Int(args, "page") ?? 1}):\n");
        foreach (var item in page.Items)
        {
            sb.Append($"- {item.Title} — Workshop id {item.Id}");
            if (item.AuthorName is { } a) sb.Append($", by {a}");
            sb.Append(FormattableString.Invariant($" · {item.Subscribers:N0} subscribers · updated {item.UpdatedUtc:yyyy-MM-dd}"));
            if (item.Versions.Count > 0) sb.Append(" · ").Append(string.Join("/", item.Versions));
            if (item.IsTranslation) sb.Append(" · translation");
            if (item.GuessRequiredDlcs() is { Count: > 0 } dlc) sb.Append(" · may need ").Append(string.Join(", ", dlc));
            if (installedIds.TryGetValue(item.Id, out var pid)) sb.Append($" · INSTALLED [{pid}]{(s.Active.Contains(pid) ? " active" : "")}");
            if (s.Extras.Replacements?.For(item.Id) is { } r) sb.Append($" · abandoned, use \"{r.NewName}\" ({r.NewWorkshopId}) instead");
            sb.AppendLine();
            var summary = BbCode.ToPlainText(item.Description).ReplaceLineEndings(" ");
            if (summary.Length > 0) sb.AppendLine("  " + (summary.Length > 200 ? summary[..200] + "…" : summary));
        }
        if (page.HasMore) sb.Append($"More results: page={(Int(args, "page") ?? 1) + 1}.");
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private static async Task<ToolResult> AnalyzeLogAsync(RimForgeState s, JsonElement args, CancellationToken ct)
    {
        var file = s.Paths.ConfigDir is { } dir ? Path.Combine(dir, Bool(args, "previous") == true ? "Player-prev.log" : "Player.log") : null;
        if (file is null || !File.Exists(file)) return new ToolResult("There is no game log yet: start the game once.", true);
        string text;
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            text = await new StreamReader(stream).ReadToEndAsync(ct);

        // Building the index reads every active mod's files; it is kept while the list stays the same.
        var hash = string.Join("\n", s.Active).GetHashCode();
        var index = _index is { } cached && cached.Hash == hash ? cached.Index
            : await Task.Run(() => ModIndex.Build(s.Active, s.Installed, s.GameVersion ?? "1.6", cancel: ct), ct);
        _index = (hash, index);

        var findings = LogAnalyzer.Analyze(text, index);
        if (findings.Count == 0) return new ToolResult($"The log ({File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}) has no errors.");
        var sb = new StringBuilder($"Log of {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}: {findings.Count} kinds of errors, worst first.\n");
        foreach (var f in findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Count).Take(25))
        {
            var message = f.Message.ReplaceLineEndings(" ");
            sb.AppendLine($"- [{f.Severity}, {f.Kind}, ×{f.Count}] {(message.Length > 300 ? message[..300] + "…" : message)}");
            if (f.Suspects.Count > 0)
                sb.AppendLine("  Likely from: " + string.Join("; ", f.Suspects.GroupBy(x => x.ModId).Take(5)
                    .Select(g => $"{s.NameOf(g.Key)} [{g.Key}] ({string.Join(", ", g.Select(x => x.Reason))})")));
        }
        if (findings.Count > 25) sb.AppendLine($"… and {findings.Count - 25} more kinds.");
        return new ToolResult(sb.ToString().TrimEnd());
    }

    // ───────────────────────── Changes (through the window) ─────────────────────────

    private async Task<ToolResult> WriteAsync(RimForgeState s, string name, JsonElement args, string? client, CancellationToken ct)
    {
        var request = name switch
        {
            WriteActions.Enable => new WriteRequest { Action = name, Ids = Installed(s, Ids(args, "ids"), mustBeActive: false) },
            WriteActions.Disable => new WriteRequest { Action = name, Ids = Disableable(s, Ids(args, "ids")) },
            WriteActions.SetLoadOrder => new WriteRequest { Action = name, Ids = LoadOrder(s, Ids(args, "ids")) },
            WriteActions.Sort => new WriteRequest { Action = name, Mode = Str(args, "mode") == "full" ? "full" : "careful" },
            WriteActions.AddRule => Rule(s, args),
            WriteActions.Pin => new WriteRequest
            {
                Action = name,
                Ids = Installed(s, [Str(args, "id") ?? throw new ArgumentException("Give the mod id.")], mustBeActive: true),
                Mode = Str(args, "place") is "top" or "bottom" or "none" ? Str(args, "place") : throw new ArgumentException("place must be top, bottom or none."),
            },
            WriteActions.CreateModpack => NewModpack(s, args),
            WriteActions.SwitchModpack => new WriteRequest { Action = name, Name = Modpack(s, Str(args, "name")).Id },
            WriteActions.Download => new WriteRequest { Action = name, WorkshopIds = WorkshopIdList(args), Flag = Bool(args, "enable") ?? true },
            WriteActions.Launch => new WriteRequest { Action = name },
            _ => throw new ArgumentException($"Unknown tool '{name}'."),
        };
        var result = await host.WriteAsync(request with { Client = client }, ct);
        return new ToolResult(result.Message, !result.Ok);
    }

    private static List<string> Ids(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString()).Where(x => x.Length > 0).ToList()
            : throw new ArgumentException($"'{name}' must be a list of mod ids.");

    private static List<string> Installed(RimForgeState s, List<string> ids, bool mustBeActive)
    {
        var found = ids.Select(id => (Id: id, Mod: s.Find(id))).ToList();
        if (found.Where(f => f.Mod is null).Select(f => f.Id).ToList() is { Count: > 0 } unknown)
            throw new ArgumentException($"Not installed: {string.Join(", ", unknown)}. Find ids with list_mods; mods from the Workshop need download_mods first.");
        var result = found.Select(f => f.Mod!.PackageId).Distinct().ToList();
        if (mustBeActive && result.Where(id => !s.Active.Contains(id)).ToList() is { Count: > 0 } off)
            throw new ArgumentException($"Not active: {string.Join(", ", off)}.");
        return result;
    }

    private static List<string> Disableable(RimForgeState s, List<string> ids)
    {
        var result = Installed(s, ids, mustBeActive: false);
        if (result.Contains(KnownMods.Core)) throw new ArgumentException("The game itself (ludeon.rimworld) can't be turned off.");
        return result.Where(s.Active.Contains).ToList() is { Count: > 0 } on ? on : throw new ArgumentException("None of these mods is active.");
    }

    private static List<string> LoadOrder(RimForgeState s, List<string> ids)
    {
        var result = Installed(s, ids, mustBeActive: false);
        if (!result.Contains(KnownMods.Core)) throw new ArgumentException("The list must contain the game itself: ludeon.rimworld.");
        return result;
    }

    private static WriteRequest Rule(RimForgeState s, JsonElement args)
    {
        var both = Installed(s, [Str(args, "before") ?? "", Str(args, "after") ?? ""], mustBeActive: true);
        if (both.Count != 2) throw new ArgumentException("'before' and 'after' must be two different active mods.");
        return new WriteRequest { Action = WriteActions.AddRule, Before = both[0], After = both[1] };
    }

    private static WriteRequest NewModpack(RimForgeState s, JsonElement args)
    {
        var name = Str(args, "name")?.Trim() is { Length: > 0 } n ? n : throw new ArgumentException("Give the modpack a name.");
        List<string>? ids = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            ids = [];
            var unknown = new List<string>();
            foreach (var raw in Ids(args, "ids"))
            {
                if (raw.StartsWith("workshop:", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(raw[9..], out var wid))
                    ids.Add(s.Find(raw)?.PackageId ?? "workshop:" + wid);
                else if (s.Find(raw) is { } mod) ids.Add(mod.PackageId);
                else unknown.Add(raw);
            }
            if (unknown.Count > 0)
                throw new ArgumentException($"Not installed: {string.Join(", ", unknown)}. Use 'workshop:<id>' for mods to download.");
            if (!ids.Contains(KnownMods.Core)) ids.Insert(0, KnownMods.Core);
        }
        return new WriteRequest { Action = WriteActions.CreateModpack, Name = name, Ids = ids?.Distinct().ToList(), Flag = Bool(args, "switch") ?? true };
    }

    private static Core.Profiles.Profile Modpack(RimForgeState s, string? name) =>
        s.Profiles.FirstOrDefault(p => p.Id == name)
        ?? s.Profiles.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.CurrentCultureIgnoreCase))
        ?? throw new ArgumentException($"No modpack '{name}'. list_modpacks shows them.");

    private static List<ulong> WorkshopIdList(JsonElement args) =>
        Ids(args, "workshop_ids").Select(x => WorkshopIds.Parse(x) ?? (ulong.TryParse(x, out var id) ? id : throw new ArgumentException($"'{x}' is not a Workshop id.")))
            .Distinct().ToList();

    // ── Argument helpers ──

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
