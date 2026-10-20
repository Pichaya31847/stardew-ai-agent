using System;
using System.Linq;
using StardewMCP.Agent.Brain;
using StardewModdingAPI;

namespace StardewMCP.Agent;

/// <summary>
/// Registers the mod's settings with Generic Mod Config Menu when it is installed, so a viewer can pick who sits in
/// each seat, which AI plays it, and whether the world freezes while the AIs think, without editing config.json.
/// </summary>
public static class ConfigMenu
{
    private const string GmcmId = "spacechase0.GenericModConfigMenu";

    /// <summary>What the API key field shows once a key is set; saving the field with this value keeps the stored key.</summary>
    private const string KeyMask = "••••••••";

    public static void Register(IModHelper helper, IManifest manifest, ModConfig config, IMonitor monitor)
    {
        var api = helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GmcmId);
        if (api == null)
        {
            monitor.Log("Generic Mod Config Menu is not installed: edit config.json to change the settings.", LogLevel.Debug);
            return;
        }

        api.Register(manifest,
            reset: () => CopyInto(new ModConfig(), config),
            save: () =>
            {
                helper.WriteConfig(config);
                Brains.OnConfigSaved();
            });

        api.AddSectionTitle(manifest, () => "Players");
        api.AddParagraph(manifest, () =>
            "Up to four screens share the farm: P1 is the save's own farmer (you, or an AI), P2-P4 are farmhands. " +
            "Seat and screen changes take effect when a save is loaded; AI settings restart the AI at once.");
        api.AddBoolOption(manifest, () => config.SplitScreen, v => config.SplitScreen = v,
            () => "Split-screen co-op", () => "Join the P2-P4 seats below as local players (needs a cabin per farmhand; see 'Build cabins').");
        api.AddBoolOption(manifest, () => config.AutoBuildCabins, v => config.AutoBuildCabins = v,
            () => "Build cabins", () => "Build the missing cabins on the farm when the save has too few (changes the save).");
        for (int i = 0; i < 4; i++)
        {
            int seat = i;
            api.AddPageLink(manifest, $"seat{seat}", () => $"Player {seat + 1} settings", () => SeatSummary(config, seat));
        }
        api.AddNumberOption(manifest, () => config.MaxOutputTokens, v => config.MaxOutputTokens = v,
            () => "Longest answer (tokens)", () => "Built-in AI: the most tokens the model may write in one turn.", 256);
        api.AddNumberOption(manifest, () => config.DailyTokenBudget, v => config.DailyTokenBudget = v,
            () => "Daily token budget", () => "Built-in AI: once a seat has used this many tokens (input + output) in one day it stops until the next morning and the others play on. 0 = no limit.", 0);

        api.AddSectionTitle(manifest, () => "Time");
        api.AddBoolOption(manifest, () => config.FreezeTimeWhileThinking, v => config.FreezeTimeWhileThinking = v,
            () => "Freeze the world while an AI thinks",
            () => "On: the clock and everyone stop while any AI is between actions (a slow model costs no game time, but you stand still too). Off: the world keeps running and the AIs have to keep up.");
        api.AddNumberOption(manifest, () => config.ThinkTimeoutSeconds, v => config.ThinkTimeoutSeconds = v,
            () => "Thinking timeout (seconds)", () => "An AI that thinks longer than this stops holding the world frozen. 0 = never.", 0, 900, 15);

        api.AddSectionTitle(manifest, () => "Display");
        api.AddNumberOption(manifest, () => config.ChatSeconds, v => config.ChatSeconds = v,
            () => "Chat line seconds", () => "Longest time an AI's `say` stays up, as the bubble above its head and the line in the chat box.", 1, 10, 1);
        api.AddBoolOption(manifest, () => config.ShowHud, v => config.ShowHud = v,
            () => "Show AI thoughts on screen", () => "A panel on each AI's screen with its thoughts, chat and current action.");
        api.AddTextOption(manifest, () => config.ThoughtLanguage, v => config.ThoughtLanguage = v,
            () => "Language of the AI's thoughts", () => "E.g. English, Thai, Japanese. Empty = the game's language.");
        api.AddBoolOption(manifest, () => config.LockHostInput, v => config.LockHostInput = v,
            () => "Lock keyboard and mouse on an AI's P1", () => "Split-screen with an AI at P1: ignore the real keyboard, mouse and pad so nobody moves its farmer by accident.");
        api.AddNumberOption(manifest, () => config.HttpPort, v => config.HttpPort = v,
            () => "HTTP port", () => "For the overlay, replay and external MCP clients (http://localhost:PORT). Restart the game after changing it.", 1024, 65535);

        for (int i = 0; i < 4; i++)
            SeatPage(api, manifest, config, i);
    }

    private static void SeatPage(IGenericModConfigMenuApi api, IManifest manifest, ModConfig config, int seat)
    {
        api.AddPage(manifest, $"seat{seat}", () => $"Player {seat + 1}");
        SeatConfig S() => config.Seat(seat);

        var roles = seat == 0 ? new[] { "ai", "human" } : new[] { "ai", "camera", "off" };
        api.AddTextOption(manifest, () => Norm(S().Role, roles), v => S().Role = v,
            () => "Who plays this seat", () => seat == 0
                ? "ai: an AI plays the save's farmer. human: you play P1 with keyboard and mouse while the AIs take the other seats."
                : "ai: an AI farmer. camera: a parked farmer whose screen only watches. off: no screen for this seat.",
            roles, RoleLabel);
        if (seat > 0)
            api.AddTextOption(manifest, () => S().Name, v => S().Name = v,
                () => "Farmhand name", () => "The farmhand this seat plays (an existing one with that name is reused).");

        api.AddTextOption(manifest, () => Norm(S().Brain, LlmProviders.Names), v => S().Brain = v,
            () => "AI provider", () =>
                "anthropic: Claude. openai: GPT, or any OpenAI-compatible server (OpenRouter, Ollama, LM Studio) with a base URL. " +
                "gemini: Google Gemini. none: an external MCP client (Claude Code, the studio runner) drives the seat through /mcp/pN.",
            LlmProviders.Names, BrainLabel);
        api.AddTextOption(manifest, () => S().Model, v => S().Model = v,
            () => "Model", () => $"Empty = the provider's default (anthropic {LlmProviders.DefaultModel("anthropic")}, openai {LlmProviders.DefaultModel("openai")}, gemini {LlmProviders.DefaultModel("gemini")}).");
        api.AddTextOption(manifest, () => string.IsNullOrEmpty(S().ApiKey) ? "" : KeyMask,
            v => { if (v.Trim() != KeyMask) S().ApiKey = v.Trim(); },
            () => "API key", () => "Your own key for the provider. It is stored as plain text in the mod's config.json; every action the AI takes costs tokens on your account.");
        api.AddTextOption(manifest, () => S().BaseUrl, v => S().BaseUrl = v.Trim(),
            () => "Base URL (optional)", () => "Endpoint override, e.g. http://localhost:11434/v1 for Ollama with provider openai, or https://openrouter.ai/api/v1.");
    }

    private static string SeatSummary(ModConfig config, int seat)
    {
        var s = config.Seat(seat);
        string who = RoleLabel(s.Role);
        string brain = s.HasBrain ? $"{s.Brain} {(string.IsNullOrWhiteSpace(s.Model) ? LlmProviders.DefaultModel(s.Brain.ToLowerInvariant()) : s.Model)}" : "external MCP client";
        return s.IsOff || s.IsHuman || s.IsCamera ? who : $"{who}: {brain}";
    }

    private static string RoleLabel(string role) => role.ToLowerInvariant() switch
    {
        "human" => "You (keyboard and mouse)",
        "camera" => "Camera (watches only)",
        "off" => "Off (no screen)",
        _ => "AI farmer"
    };

    private static string BrainLabel(string brain) => brain.ToLowerInvariant() switch
    {
        "anthropic" => "Anthropic (Claude)",
        "openai" => "OpenAI / OpenAI-compatible",
        "gemini" => "Google Gemini",
        _ => "None (external MCP client)"
    };

    private static string Norm(string value, string[] allowed) =>
        allowed.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? allowed[0];

    /// <summary>Reset: copy every setting of <paramref name="from"/> into the live config object (it is shared by reference).</summary>
    private static void CopyInto(ModConfig from, ModConfig to)
    {
        foreach (var p in typeof(ModConfig).GetProperties().Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0))
            p.SetValue(to, p.GetValue(from));
    }
}
