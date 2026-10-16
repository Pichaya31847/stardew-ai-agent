namespace StardewMCP.Agent;

/// <summary>User-editable settings (config.json in the mod folder).</summary>
public class ModConfig
{
    /// <summary>Port for the MCP endpoint (/mcp), overlay (/overlay) and replay (/replay).</summary>
    public int HttpPort { get; set; } = 8770;

    /// <summary>Stop the in-game clock while no agent action is running (the AI is thinking).</summary>
    public bool FreezeTimeWhileThinking { get; set; } = true;

    /// <summary>Folder for per-day JSONL logs and diaries. Empty = "logs" inside the mod folder.</summary>
    public string LogDir { get; set; } = "";

    /// <summary>Save folder name (e.g. "Duck_123456789") to load automatically from the title screen. Empty = off.</summary>
    public string AutoLoadSave { get; set; } = "";

    /// <summary>Language the AI is told to use for on-screen text: thoughts, action reasons and the diary (e.g. "English").</summary>
    public string ThoughtLanguage { get; set; } = "Thai";
}
