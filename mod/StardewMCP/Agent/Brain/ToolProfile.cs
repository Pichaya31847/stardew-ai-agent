using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// Which tool descriptions the built-in AI is sent (config BrainTools). "compact" keeps the everyday tools whole and cuts
/// the others to one sentence with bare argument names: on the local 32k Qwen run the tool list and the system prompt
/// filled about 20k tokens, so the seat restarted its conversation about 18 times a day. Every tool stays callable.
/// Game-free, so the unit tests compile this file.
/// </summary>
public static class ToolProfile
{
    /// <summary>The tools a farming day needs, sent whole in compact mode.</summary>
    public static readonly IReadOnlyCollection<string> Core = new HashSet<string>(StringComparer.Ordinal)
    {
        "think", "status", "look", "inventory", "walk_to", "travel_to", "use_tool", "plant", "harvest", "harvest_all",
        "water_all", "clear_area", "till_area", "farm_summary", "interact", "respond", "close_menu", "menu", "shop_list",
        "buy", "sell", "ship", "eat", "chest", "craft", "place", "rest", "wait", "go_to_bed", "journal", "say", "give",
        "talk_to", "villagers", "quests", "accept_quest", "festival", "calendar", "crops", "fish", "fight", "leave_mine",
        "morning_chores", "forage", "upgrade_backpack", "bundles", "community_center", "donate_bundle"
    };

    private const int BriefMax = 160;

    public static bool IsCompact(string? profile) => string.Equals(profile?.Trim(), "compact", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tools as configured: unchanged for "full" (or anything unknown), shortened for "compact".</summary>
    public static List<LlmTool> Apply(string? profile, IEnumerable<LlmTool> tools) =>
        IsCompact(profile) ? tools.Select(t => Core.Contains(t.Name) ? ShortReason(t) : Brief(t)).ToList() : tools.ToList();

    /// <summary>The first sentence of the description, and the schema without argument descriptions (types, enums and required stay).</summary>
    public static LlmTool Brief(LlmTool tool)
    {
        var node = JsonNode.Parse(tool.Schema.GetRawText())!;
        StripDescriptions(node, top: true);
        return tool with { Description = FirstSentence(tool.Description), Schema = JsonSerializer.SerializeToElement(node) };
    }

    /// <summary>A core tool with only its reason argument's long description shortened (the system prompt says what a reason is).</summary>
    public static LlmTool ShortReason(LlmTool tool)
    {
        var node = JsonNode.Parse(tool.Schema.GetRawText())!;
        if (node["properties"]?["reason"] is JsonObject reason && reason.ContainsKey("description"))
            reason["description"] = "Short reason, one line";
        return tool with { Schema = JsonSerializer.SerializeToElement(node) };
    }

    public static string FirstSentence(string text)
    {
        text = text.Trim();
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        var first = end > 0 ? text[..(end + 1)] : text;
        return first.Length <= BriefMax ? first : first[..(BriefMax - 1)].TrimEnd() + "…";
    }

    /// <param name="top">The tool's own schema or a map of property names (an argument may be called "description"): keep its keys.</param>
    private static void StripDescriptions(JsonNode? node, bool top)
    {
        switch (node)
        {
            case JsonObject obj:
                if (!top) obj.Remove("description");
                foreach (var (key, child) in obj.ToList())
                    StripDescriptions(child, top: key == "properties");
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    StripDescriptions(child, top: false);
                break;
        }
    }
}
