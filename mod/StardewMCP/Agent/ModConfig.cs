using System;
using StardewValley;

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

/// <summary>User-editable settings (config.json in the mod folder).</summary>
public class ModConfig
{
    public bool PrefillNewGame { get; set; } = false;
    public string NewGamePlayerName { get; set; } = "AI1";
    public string NewGameFarmName { get; set; } = "AI Farm";
    public string NewGameFavoriteThing { get; set; } = "Farming";
    public bool NewGameSkipIntro { get; set; } = true;

    /// <summary>
    /// The four seats P1-P4 (P1 is the save's own farmer and the host screen). Defaults: P1-P3 AI seats driven from
    /// outside through MCP, P4 the camera. Set a seat's Brain and ApiKey to let the mod play it by itself.
    /// </summary>
    public SeatConfig[] Seats { get; set; } =
    {
        new SeatConfig(),
        new SeatConfig { Name = "Gemini" },
        new SeatConfig { Name = "GPT" },
        new SeatConfig { Role = "camera", Name = "Camera" }
    };

    /// <summary>Seat config by screen index, never null (a short Seats array is padded with defaults).</summary>
    public SeatConfig Seat(int screen)
    {
        if (Seats == null || Seats.Length < 4)
        {
            var defaults = new ModConfig().Seats;
            var full = new SeatConfig[4];
            for (int i = 0; i < 4; i++)
                full[i] = Seats != null && i < Seats.Length && Seats[i] != null ? Seats[i] : defaults[i];
            Seats = full;
        }
        return Seats[Math.Clamp(screen, 0, 3)] ??= new SeatConfig();
    }

    /// <summary>Show each AI seat's thoughts, chat and current action on its own screen (no OBS needed).</summary>
    public bool ShowHud { get; set; } = true;
    /// <summary>Use a camera seat as an operator dashboard with all AI logs and an order box.</summary>
    public bool OperatorDashboard { get; set; } = false;
    /// <summary>"bottom" puts two AI views above a full-width camera dashboard; "quadrant" keeps the original grid.</summary>
    public string DashboardLayout { get; set; } = "quadrant";

    /// <summary>
    /// Split-screen: give every farmer their own wallet. On a save with a shared wallet the host asks for it on load, the way
    /// the Mayor's ledger does, and the game splits the money evenly that night (changes the save).
    /// </summary>
    public bool SeparateWallets { get; set; } = false;

    /// <summary>Split-screen: build the cabins the farmhands need when the farm has too few (changes the save).</summary>
    public bool AutoBuildCabins { get; set; } = true;


    /// <summary>Built-in AI: longest answer the model may write per turn (tokens).</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>Built-in AI: once a seat has used this many tokens (input + output) in one day it stops until the next morning. 0 = no limit.</summary>
    public int DailyTokenBudget { get; set; } = 0;

    /// <summary>
    /// Built-in AI: "full" sends every tool with its whole description; "compact" sends the everyday tools in full and the
    /// rest with one-line descriptions and bare argument names, for small local models with a short context (a 32k Qwen
    /// spent most of its window on the tool list). Every tool still works either way.
    /// </summary>
    public string BrainTools { get; set; } = "full";

    /// <summary>
    /// Built-in AI, OpenAI-compatible servers: reasoning_effort sent with every request ("none", "low", "medium", "high").
    /// "none" turns off Qwen3's hidden reasoning in Ollama (10x faster replies). Empty = the server's default.
    /// </summary>
    public string BrainReasoning { get; set; } = "";

    /// <summary>Built-in AI: how many of the last lines of the lessons and notes files go into the system prompt.</summary>
    public int BrainMemoryLines { get; set; } = 60;

    /// <summary>Built-in AI: run morning_chores (gift boxes, mailbox, quest money) before the model's first turn each day.</summary>
    public bool BrainMorningChores { get; set; } = true;

    /// <summary>Built-in AI: take today's Help Wanted request in the morning when it is still free and doable with what the farmer has (walks to the board in Town).</summary>
    public bool BrainAcceptBoardQuest { get; set; } = false;

    /// <summary>Port for the MCP endpoint (/mcp), overlay (/overlay) and replay (/replay).</summary>
    public int HttpPort { get; set; } = 8770;

    /// <summary>Stop the in-game clock while no agent action is running (the AI is thinking). Starts with the seat's first AI contact, so a seat nobody plays never holds the clock.</summary>
    public bool FreezeTimeWhileThinking { get; set; } = true;

    /// <summary>Folder for per-day JSONL logs and diaries. Empty = "logs" inside the mod folder.</summary>
    public string LogDir { get; set; } = "";

    /// <summary>Save folder name (e.g. "Duck_123456789") to load automatically from the title screen. Empty = off.</summary>
    public string AutoLoadSave { get; set; } = "";

    /// <summary>Serve POST /api/debug/* (warp, set_time, add_item...) for runner/smoke.py. Keep off for normal play.</summary>
    public bool EnableDebugApi { get; set; } = false;

    /// <summary>Language the AI is told to use for on-screen text: thoughts, action reasons and the diary (e.g. "English"). Empty = the game's language.</summary>
    public string ThoughtLanguage { get; set; } = "";

    /// <summary>
    /// Language of what the AI says out loud (say: chat and bubbles), e.g. "Thai" with English thoughts, which cost a small
    /// model far fewer tokens. Empty = the same as ThoughtLanguage.
    /// </summary>
    public string SpeechLanguage { get; set; } = "";

    /// <summary>The configured SpeechLanguage, or <see cref="LanguageName"/> when it is blank.</summary>
    public string SpeechLanguageName() => string.IsNullOrWhiteSpace(SpeechLanguage) ? LanguageName() : SpeechLanguage.Trim();

    /// <summary>
    /// Language of the AI's working text: action reasons and the memory files it reads back every day (lessons, plan,
    /// notes, self, people), e.g. "English" while thoughts and the diary stay in Thai. Those files go into every system
    /// prompt, so English keeps a small model's context short. Empty = the same as ThoughtLanguage.
    /// </summary>
    public string WorkLanguage { get; set; } = "";

    /// <summary>The configured WorkLanguage, or <see cref="LanguageName"/> when it is blank.</summary>
    public string WorkLanguageName() => string.IsNullOrWhiteSpace(WorkLanguage) ? LanguageName() : WorkLanguage.Trim();

    /// <summary>The configured ThoughtLanguage, or the game's current language when it is blank.</summary>
    public string LanguageName()
    {
        if (!string.IsNullOrWhiteSpace(ThoughtLanguage))
            return ThoughtLanguage.Trim();
        try
        {
            var code = LocalizedContentManager.CurrentLanguageCode;
            if (code == LocalizedContentManager.LanguageCode.mod)
            {
                var tag = LocalizedContentManager.CurrentModLanguage?.LanguageCode ?? "";
                return tag.Split('-')[0].ToLowerInvariant() switch
                {
                    "th" => "Thai", "vi" => "Vietnamese", "id" => "Indonesian", "pl" => "Polish", "uk" => "Ukrainian",
                    "nl" => "Dutch", "sv" => "Swedish", "cs" => "Czech", "ro" => "Romanian", "ar" => "Arabic", "he" => "Hebrew",
                    _ => "English"
                };
            }
            return code switch
            {
                LocalizedContentManager.LanguageCode.ja => "Japanese",
                LocalizedContentManager.LanguageCode.zh => "Chinese",
                LocalizedContentManager.LanguageCode.ru => "Russian",
                LocalizedContentManager.LanguageCode.de => "German",
                LocalizedContentManager.LanguageCode.es => "Spanish",
                LocalizedContentManager.LanguageCode.fr => "French",
                LocalizedContentManager.LanguageCode.hu => "Hungarian",
                LocalizedContentManager.LanguageCode.it => "Italian",
                LocalizedContentManager.LanguageCode.ko => "Korean",
                LocalizedContentManager.LanguageCode.pt => "Portuguese",
                LocalizedContentManager.LanguageCode.tr => "Turkish",
                _ => "English"
            };
        }
        catch
        {
            return "English";
        }
    }

    /// <summary>
    /// water_all, clear_area, till_area (and the Scythe pass of harvest_all) stop when energy drops below this, instead of
    /// working until the farmer is exhausted. They never eat; the AI decides.
    /// </summary>
    public int FarmBatchMinEnergy { get; set; } = 10;

    /// <summary>
    /// Three-AI split-screen: after the save loads, join P2–P4 as local farmhands (needs 3 free
    /// cabins). P1–P3 are AIs on /mcp/p1../mcp/p3, P4 is the video's camera screen.
    /// </summary>
    public bool SplitScreen { get; set; } = false;

    /// <summary>Farmhand names for P2, P3 and P4. An existing farmhand with the same name is reused.</summary>
    public string[] FarmhandNames { get; set; } = { "Gemini", "GPT", "Camera" };

    /// <summary>Split-screen: an AI that thinks longer than this (seconds) stops holding the world frozen. 0 = never.</summary>
    public int ThinkTimeoutSeconds { get; set; } = 300;

    /// <summary>Loop guard (#31): add a "try something different" hint once the identical call has failed this many times in a row. 0 = off.</summary>
    public int RepeatHintAfter { get; set; } = 3;

    /// <summary>Loop guard (#31): the Nth identical call in a row is refused without running when the N-1 before it all failed (the 5th by default). 0 = off.</summary>
    public int RepeatBlockAfter { get; set; } = 5;

    /// <summary>Loop guard (#31): add a "pick an action" hint after this many calls in a row with no game time, inventory or position change. 0 = off.</summary>
    public int NoProgressAfter { get; set; } = 25;

    /// <summary>Split-screen: ignore the real keyboard, mouse and pad on P1 so nobody at the PC moves it or unpauses.</summary>
    public bool LockHostInput { get; set; } = true;

    /// <summary>Split-screen: frozen frames look like live ones (no Paused banner, HUD kept) and hide cursors, the gamepad hint and join messages.</summary>
    public bool CleanFrames { get; set; } = true;

    /// <summary>Split-screen: set each screen's zoom to keep about 20 tiles in view (1.5 × screen width / 960).</summary>
    public bool AutoZoom { get; set; } = true;

    /// <summary>Split-screen: from this time (HHMM, e.g. 2200) an AI that is not in bed gets one warning per day that the others wait for it. 0 = off.</summary>
    public int BedtimeWarning { get; set; } = 2200;

    /// <summary>
    /// How the fish tool plays the reel-in minigame. "perfect" writes the bar straight onto the fish (never loses one);
    /// "natural" presses and releases the use-tool button, so the bar moves with the game's own physics and fish can
    /// get away. "natural" patches BobberBar.update with Harmony the first time fish runs; "perfect" patches nothing.
    /// </summary>
    public string FishingAssist { get; set; } = "perfect";

    /// <summary>Profession picked when the AI hasn't used choose_profession by bedtime: 0 = left option, 1 = right.</summary>
    public int DefaultProfession { get; set; } = 0;

    /// <summary>
    /// Let villager heart events play out (their dialogue goes to the log and questions are left to the AI's respond)
    /// instead of skipping every cutscene. Off by default until it is tested in split-screen. See NpcStory.cs.
    /// </summary>
    public bool PlayHeartEvents { get; set; } = false;

    /// <summary>
    /// villagers(name) shows what a villager thinks of every item you carry, straight from the game data. Off by default:
    /// gift tastes are discovered by giving gifts (and by what other players tell you). See NpcKnowledge.cs.
    /// </summary>
    public bool RevealGiftTastes { get; set; } = false;

    /// <summary>
    /// Session breaks (#30): game times (HHMM) at which a seat's AI is told to continue in a new session, so old maps and
    /// results leave its context. Only applies to MCP clients that ask for it with <c>/mcp?breaks=1</c> (runner/run_days.py does).
    /// Empty = no time breaks.
    /// </summary>
    public int[] SessionBreakTimes { get; set; } = { 1200, 1800 };

    /// <summary>Session breaks: also break after this many tool calls in one session. 0 = off.</summary>
    public int SessionBreakToolCalls { get; set; } = 150;

    /// <summary>Session breaks: also break once the results returned in one session add up to this many characters (about 4 per token). 0 = off.</summary>
    public int SessionBreakResultChars { get; set; } = 120000;

    /// <summary>Session breaks apply to every MCP client, not just those that connect with <c>?breaks=1</c>.</summary>
    public bool SessionBreaksForAll { get; set; } = false;

    /// <summary>Key (an XNA <c>Keys</c> name, e.g. "F8") that toggles pause / resume while the game window has focus. Empty = no hotkey.</summary>
    public string PauseKey { get; set; } = "F8";

    /// <summary>Longest time in seconds an AI's `say` stays up: the bubble above its head and its line in the chat box
    /// (the game keeps chat lines 10 s, too long next to the other players' talk).</summary>
    public int ChatSeconds { get; set; } = 5;
}
