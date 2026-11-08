using System;
using System.Collections.Generic;

namespace StardewMCP.Agent;

/// <summary>One split-screen seat in config.json: who sits there and, for an AI, which model plays it.</summary>
public class SeatConfig
{
    /// <summary>"ai" (a farmer played by an AI), "human" (P1 only: you, with keyboard and mouse), "camera" (a parked farmer whose screen only watches) or "off" (no screen).</summary>
    public string Role { get; set; } = "ai";

    /// <summary>
    /// The AI that plays this seat: "anthropic", "openai" (also any OpenAI-compatible server: OpenRouter, Ollama, LM Studio...),
    /// "gemini", or "none" = an external MCP client drives the seat through /mcp/pN (Claude Code, the studio runner).
    /// </summary>
    public string Brain { get; set; } = "none";

    /// <summary>Model name for the provider; empty = the provider's default.</summary>
    public string Model { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>Optional endpoint override, e.g. http://localhost:11434/v1 for Ollama with Brain "openai".</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Farmhand name (P2-P4); empty = FarmhandNames / "P{N}".</summary>
    public string Name { get; set; } = "";

    public bool IsOff => Role.Equals("off", StringComparison.OrdinalIgnoreCase);
    public bool IsHuman => Role.Equals("human", StringComparison.OrdinalIgnoreCase);
    public bool IsCamera => Role.Equals("camera", StringComparison.OrdinalIgnoreCase);
    public bool HasBrain => !string.IsNullOrWhiteSpace(Brain) && !Brain.Equals("none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The <c>Screens</c> setting (1-4 farmers) as seat roles, split-screen and the command panel (operator dashboard):
/// 1 and 4 have no command panel, 2 and 3 have it in the free part of the window.
/// </summary>
public static class ScreenPlan
{
    private static readonly string[] SeatOnlyNames = { "Camera", "Control", "Off" };

    /// <summary>
    /// P1..PN play: an "off" or camera seat among them becomes an AI, takes the brain of the AI seat before it when it has
    /// none and gets a farmer's name. With 2 or 3 screens the seat after them is the command panel's camera; the rest are
    /// off. <paramref name="seats"/> holds the four seats and is changed in place. Returns what changed, for the log.
    /// </summary>
    public static (bool splitScreen, bool dashboard, List<string> notes) Apply(SeatConfig[] seats, int screens)
    {
        var notes = new List<string>();
        int n = Math.Clamp(screens, 1, 4);
        if (n != screens) notes.Add($"Screens {screens} is outside 1-4: {n} screens.");
        bool dashboard = n is 2 or 3;
        SeatConfig? brainFrom = null;
        for (int i = 0; i < seats.Length; i++)
        {
            var s = seats[i];
            if (i < n)
            {
                if (s.IsOff || s.IsCamera || (i > 0 && s.IsHuman))
                {
                    notes.Add($"P{i + 1} plays (Screens {n}): role \"{s.Role}\" -> \"ai\".");
                    s.Role = "ai";
                    if (string.IsNullOrWhiteSpace(s.Name) || Array.Exists(SeatOnlyNames, x => x.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
                        s.Name = $"AI{i + 1}";
                    if (!s.HasBrain && brainFrom != null)
                    {
                        (s.Brain, s.Model, s.ApiKey, s.BaseUrl) = (brainFrom.Brain, brainFrom.Model, brainFrom.ApiKey, brainFrom.BaseUrl);
                        notes.Add($"P{i + 1} uses the same AI as the seat before it ({s.Brain} {s.Model}).");
                    }
                }
                if (!s.IsHuman && s.HasBrain) brainFrom = s;
            }
            else
                s.Role = i == n && dashboard ? "camera" : "off";
        }
        return (n > 1, dashboard, notes);
    }
}
