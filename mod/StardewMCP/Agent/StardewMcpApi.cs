using System;

namespace StardewMCP.Agent;

/// <summary>
/// The mod API other SMAPI mods get with <c>Helper.ModRegistry.GetApi&lt;IStardewMcpApi&gt;("Duckxy.StardewMCP")</c> (copy this
/// interface into your mod). Call it in your <c>GameLaunched</c> handler.
/// </summary>
public interface IStardewMcpApi
{
    /// <summary>
    /// Add a tool every AI seat can call; a tool with a built-in tool's name takes its place. <paramref name="inputSchemaJson"/>
    /// is the tool's JSON Schema (an object; actions need a string <c>reason</c> property). <paramref name="run"/> gets the
    /// arguments as JSON and returns the result text; it runs on the game thread with <c>Game1.player</c> set to the calling
    /// farmer, and throws to report a failure (the message is the result). <paramref name="instant"/> tools only observe and
    /// keep the world frozen; <paramref name="core"/> tools are sent whole in the compact tool profile.
    /// </summary>
    void AddTool(string name, string description, string inputSchemaJson, Func<string, string> run, bool instant, bool core);

    /// <summary>
    /// Add a line to every farmer's morning briefing. <paramref name="note"/> runs on the game thread with <c>Game1.player</c>
    /// set to the farmer being briefed; return "" for nothing that day.
    /// </summary>
    void AddMorningNote(Func<string> note);

    /// <summary>
    /// Items the farmer has outside the pack and the placed chests, such as machines in a slot of your mod. <paramref name="count"/>
    /// gets a qualified item id ("(BC)13") and returns how many <c>Game1.player</c> has there; <paramref name="howToUse"/> returns
    /// the line telling the AI where they are and how to use them ("" when none). The advice then stops asking for another one.
    /// </summary>
    void AddItemHolder(Func<string, int> count, Func<string, string> howToUse);

    /// <summary>Your mod pays building, house and tool upgrade materials out of the chests by itself: the advice stops asking to carry them.</summary>
    void SetStoragePays(bool pays);

    /// <summary>
    /// Write a change your mod made to a shared chest (or to a storage of chests) into the chest log of the farmer on this screen
    /// (the day log and chests.jsonl), like the built-in chest tool's changes. <paramref name="put"/>: into the storage (else out of it).
    /// </summary>
    void LogChestChange(bool put, string item, int count, string location, int x, int y);
}

public sealed class StardewMcpApi : IStardewMcpApi
{
    public void AddTool(string name, string description, string inputSchemaJson, Func<string, string> run, bool instant, bool core) =>
        ToolExtensions.Add(name, description, inputSchemaJson, run, instant, core);

    public void AddMorningNote(Func<string> note) => ToolExtensions.AddMorningNote(note);

    public void AddItemHolder(Func<string, int> count, Func<string, string> howToUse) => ToolExtensions.AddItemHolder(count, howToUse);

    public void SetStoragePays(bool pays) => ToolExtensions.StoragePays = pays;

    public void LogChestChange(bool put, string item, int count, string location, int x, int y) =>
        ChestLedger.External(put, item, count, location, x, y);
}
