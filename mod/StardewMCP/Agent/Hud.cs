using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// In-game HUD for people who watch without OBS: on each AI seat's screen, a small panel with the farmer's name
/// and model, what it is doing or how long it has been thinking, its latest thought and chat line.
/// Fed by <see cref="AgentLog.Added"/>; drawn in SMAPI's RenderedHud for the screen being drawn.
/// </summary>
public static class Hud
{
    private sealed class View
    {
        public string? Think;
        public DateTime ThinkAt;
        public string? Say;
        public DateTime SayAt;
    }

    private const int ThinkSeconds = 90;
    private const int ChatSeconds = 60;
    private const int Pad = 10;

    private static ModConfig _cfg = null!;
    private static readonly View[] Views = { new(), new(), new(), new() };
    private static HashSet<char>? _glyphs;
    private static SpriteFont? _glyphFont;

    public static void Init(IModHelper helper, ModConfig config)
    {
        _cfg = config;
        OperatorPanel.Init(helper, config);
        AgentLog.Added += OnEntry;
        helper.Events.Display.RenderedHud += OnRenderedHud;
    }

    private static void OnEntry(LogEntry e)
    {
        if (e.Player == null || Players.ById(e.Player) is not { } slot || string.IsNullOrWhiteSpace(e.Text))
            return;
        var v = Views[slot.Screen];
        var now = DateTime.UtcNow;
        switch (e.Kind)
        {
            case "think":
                v.Think = e.Text;
                v.ThinkAt = now;
                break;
            case "say":
                v.Say = e.Text;
                v.SayAt = now;
                break;
        }
    }

    private static void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (!_cfg.ShowHud || !Context.IsWorldReady || Game1.eventUp || Game1.activeClickableMenu != null)
            return;
        if (_cfg.OperatorDashboard)
            return; // the operator panel shows every seat without covering the farm views
        var seat = Players.Current;
        if (seat.Role != SeatRole.Ai)
            return;
        var brain = Brain.Brains.For(seat.Id);
        if (brain == null && !seat.Connected)
            return; // nobody plays this farmer: nothing to show

        var font = Game1.smallFont;
        var v = Views[seat.Screen];
        var now = DateTime.UtcNow;
        int width = Math.Min(620, Game1.uiViewport.Width - 32);
        int textWidth = width - 2 * Pad;

        var lines = new List<(string text, Color color)>();
        string who = (seat.State as ScreenState)?.Name ?? seat.Id;
        string what = brain != null ? $"{brain.Provider} {brain.Model}" : "MCP client";
        lines.Add(($"{who}  -  {what}", Color.Gold));

        string status;
        var tool = seat.Runtime.CurrentTool;
        if (brain is { Failed: true })
            status = "! " + brain.Status;
        else if (RunControl.Paused)
            status = "paused";
        else if (tool != null)
        {
            var reason = seat.Runtime.CurrentReason;
            status = "> " + tool + (string.IsNullOrWhiteSpace(reason) ? "" : "  " + reason);
        }
        else if (WorldClock.ThinkingSeconds(seat) is { } secs)
            status = $"thinking {secs:0}s" + (seat.TimedOut ? " (world runs on)" : "");
        else if (brain != null)
            status = brain.Status;
        else
            status = "waiting";
        lines.Add((status, Color.White));

        if (v.Think != null && (now - v.ThinkAt).TotalSeconds < ThinkSeconds)
            lines.Add(("* " + v.Think, Color.LightSkyBlue));
        if (v.Say != null && (now - v.SayAt).TotalSeconds < ChatSeconds)
            lines.Add(("\" " + v.Say, Color.PaleGreen));

        var wrapped = new List<(string, Color)>();
        foreach (var (text, color) in lines)
            foreach (var l in Wrap(font, Sanitize(font, text), textWidth, maxLines: 6))
                wrapped.Add((l, color));

        float lineH = font.LineSpacing;
        int height = (int)(wrapped.Count * lineH) + 2 * Pad;
        // The toolbar sits at the top of the screen while the farmer is in the lower half: draw under it then.
        int top = 16;
        foreach (var menu in Game1.onScreenMenus)
            if (menu is Toolbar bar && bar.yPositionOnScreen < Game1.uiViewport.Height / 2)
                top = Math.Max(top, bar.yPositionOnScreen + bar.height + 8);
        var box = new Rectangle(16, top, width, height);
        e.SpriteBatch.Draw(Game1.fadeToBlackRect, box, Color.Black * 0.6f);
        float y = box.Y + Pad;
        foreach (var (text, color) in wrapped)
        {
            e.SpriteBatch.DrawString(font, text, new Vector2(box.X + Pad + 1, y + 1), Color.Black * 0.8f);
            e.SpriteBatch.DrawString(font, text, new Vector2(box.X + Pad, y), color);
            y += lineH;
        }
    }

    /// <summary>Replace characters the font cannot draw (emoji, scripts without a font mod), so DrawString never throws.</summary>
    internal static string Sanitize(SpriteFont font, string text)
    {
        if (_glyphs == null || _glyphFont != font)
        {
            _glyphs = new HashSet<char>(font.Characters);
            _glyphFont = font;
        }
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r", ""))
            sb.Append(c == '\n' ? ' ' : _glyphs.Contains(c) ? c : '·');
        return sb.ToString();
    }

    /// <summary>Word wrap; a word wider than the line (Thai has no spaces) is broken by characters.</summary>
    internal static IEnumerable<string> Wrap(SpriteFont font, string text, int width, int maxLines)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureString(candidate).X <= width)
            {
                line.Clear().Append(candidate);
                continue;
            }
            if (line.Length > 0)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (font.MeasureString(word).X <= width)
            {
                line.Append(word);
                continue;
            }
            foreach (var ch in word)
            {
                if (font.MeasureString(line.ToString() + ch).X > width && line.Length > 0)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                line.Append(ch);
            }
        }
        if (line.Length > 0)
            lines.Add(line.ToString());
        if (lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            lines[maxLines - 1] += "...";
        }
        return lines;
    }
}
