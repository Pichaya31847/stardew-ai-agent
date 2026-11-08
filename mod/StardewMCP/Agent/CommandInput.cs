using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// The /do line for a person playing in Manual mode, in place of the game's chat box: the caret moves (arrows, Home, End,
/// a click in the text), Ctrl+V pastes where the caret is, Up and Down bring back earlier commands, and only Enter (run)
/// or Esc (close) end it; clicking elsewhere does not. The map tile under the mouse stays on screen while typing.
/// </summary>
internal sealed class CommandInput : IClickableMenu, IKeyboardSubscriber
{
    private const int Height = 64, Pad = 16, RepeatDelayMs = 400, RepeatMs = 40;
    private const string Prefix = "/do ";
    private static readonly Keys[] Moves = { Keys.Left, Keys.Right, Keys.Home, Keys.End, Keys.Delete, Keys.Up, Keys.Down };

    private string _text = "";
    private int _caret;
    private readonly List<string> _past;
    private int _pastAt;
    private KeyboardState _keys;
    private Keys? _held;
    private double _heldMs, _repeatMs;
    private int _ticks;

    public bool Selected { get; set; } = true;

    public CommandInput(IEnumerable<string> past, string text = "")
        : base(16, Game1.uiViewport.Height - Height - 140, Math.Min(Game1.uiViewport.Width - 32, 1200), Height)
    {
        _past = past.ToList();
        _pastAt = _past.Count;
        _text = text;
        _caret = text.Length;
        _keys = Game1.input.GetKeyboardState();
        Game1.keyboardDispatcher.Subscriber = this;
    }

    // ------------------------------------------------------------------ editing

    private void Insert(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        if (s.Length == 0) return;
        _text = _text.Insert(_caret, s);
        _caret += s.Length;
    }

    /// <summary>The key that opened the line arrives as text a moment later ("t" or "/"): it is not part of the command.</summary>
    public char? Opener;

    public void RecieveTextInput(char inputChar)
    {
        if (Opener is { } o && _ticks < 5 && char.ToLowerInvariant(inputChar) == char.ToLowerInvariant(o)) { Opener = null; return; }
        if (char.IsControl(inputChar) || Ctrl) return; // Ctrl+V comes as a control character too: the paste is done below
        Insert(inputChar.ToString());
    }

    /// <summary>A paste (Ctrl+V) arrives here from the game's keyboard dispatcher: it goes in at the caret.</summary>
    public void RecieveTextInput(string text) => Insert(text);

    public void RecieveCommandInput(char command)
    {
        switch (command)
        {
            case '\b':
                if (_caret > 0) { _text = _text.Remove(_caret - 1, 1); _caret--; }
                break;
            case '\r':
                Submit();
                break;
        }
    }

    public void RecieveSpecialInput(Keys key) { } // arrows and the rest are read from the keyboard in update, with key repeat

    private bool Ctrl => _keys.IsKeyDown(Keys.LeftControl) || _keys.IsKeyDown(Keys.RightControl);

    private void Move(Keys key)
    {
        switch (key)
        {
            case Keys.Left: _caret = Math.Max(0, _caret - 1); break;
            case Keys.Right: _caret = Math.Min(_text.Length, _caret + 1); break;
            case Keys.Home: _caret = 0; break;
            case Keys.End: _caret = _text.Length; break;
            case Keys.Delete: if (_caret < _text.Length) _text = _text.Remove(_caret, 1); break;
            case Keys.Up when _pastAt > 0: _text = _past[--_pastAt]; _caret = _text.Length; break;
            case Keys.Down when _pastAt < _past.Count:
                _pastAt++;
                _text = _pastAt < _past.Count ? _past[_pastAt] : "";
                _caret = _text.Length;
                break;
        }
    }

    private void Submit()
    {
        var command = _text.Trim();
        if (command.StartsWith("/do ", StringComparison.OrdinalIgnoreCase)) command = command[4..].Trim();
        Close();
        if (command.Length > 0) Brain.Brains.Do(command);
    }

    private void Close()
    {
        if (Game1.keyboardDispatcher.Subscriber == this) Game1.keyboardDispatcher.Subscriber = null;
        Selected = false;
        exitThisMenu(playSound: false);
    }

    // ------------------------------------------------------------------ input

    public override void update(GameTime time)
    {
        base.update(time);
        _ticks++;
        if (Game1.keyboardDispatcher.Subscriber != this) Game1.keyboardDispatcher.Subscriber = this;
        var now = Game1.input.GetKeyboardState();
        var before = _keys;
        _keys = now;
        // Ctrl+V: the game's keyboard dispatcher already hands the clipboard over as text (RecieveTextInput), which goes in
        // at the caret; pasting here as well put it in twice (2026-11-08).

        // Arrows, Home, End and Delete: once on the press, then repeating while held.
        var down = Moves.FirstOrDefault(k => now.IsKeyDown(k));
        if (down == Keys.None || !now.IsKeyDown(down)) { _held = null; return; }
        if (_held != down || !before.IsKeyDown(down))
        {
            _held = down;
            _heldMs = 0;
            _repeatMs = 0;
            Move(down);
            return;
        }
        _heldMs += time.ElapsedGameTime.TotalMilliseconds;
        if (_heldMs < RepeatDelayMs) return;
        _repeatMs += time.ElapsedGameTime.TotalMilliseconds;
        while (_repeatMs >= RepeatMs) { _repeatMs -= RepeatMs; Move(down); }
    }

    public override void receiveKeyPress(Keys key)
    {
        // Only Esc closes: E, I, M and the other menu keys are letters of the command here.
        if (key == Keys.Escape) Close();
    }

    public override bool readyToClose() => false; // a click elsewhere or a game key never closes it

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        // A click in the text puts the caret there; anywhere else it does nothing.
        if (!isWithinBounds(x, y)) return;
        var (shown, from) = Visible();
        float at = xPositionOnScreen + Pad + Game1.smallFont.MeasureString(Prefix).X;
        for (int i = 0; i <= shown.Length; i++)
        {
            float w = Game1.smallFont.MeasureString(shown[..i]).X;
            if (at + w >= x) { _caret = from + Math.Max(0, i - (i > 0 && at + w - x > 8 ? 1 : 0)); return; }
        }
        _caret = from + shown.Length;
    }

    public override void receiveRightClick(int x, int y, bool playSound = true) { }

    // ------------------------------------------------------------------ draw

    /// <summary>The part of the text that fits the box, kept around the caret, and where it starts.</summary>
    private (string shown, int from) Visible()
    {
        var font = Game1.smallFont;
        float room = width - 2 * Pad - font.MeasureString(Prefix).X - 8;
        int from = 0;
        while (from < _caret && font.MeasureString(_text[from.._caret]).X > room) from++;
        int to = _text.Length;
        while (to > _caret && font.MeasureString(_text[from..to]).X > room) to--;
        return (_text[from..to], from);
    }

    public override void draw(SpriteBatch b)
    {
        Hud.DrawCoordinates(b);
        var font = Game1.smallFont;
        drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
        float y = yPositionOnScreen + (height - font.LineSpacing) / 2f;
        float x = xPositionOnScreen + Pad;
        b.DrawString(font, Prefix, new Vector2(x, y), Color.Gray);
        x += font.MeasureString(Prefix).X;
        var (shown, from) = Visible();
        b.DrawString(font, shown, new Vector2(x, y), Game1.textColor);
        if (_ticks / 30 % 2 == 0)
        {
            float cx = x + font.MeasureString(shown[..(_caret - from)]).X;
            b.Draw(Game1.staminaRect, new Rectangle((int)cx, (int)y + 4, 2, font.LineSpacing - 8), Game1.textColor);
        }
        b.DrawString(font, "Enter: run   Esc: close   Up/Down: earlier commands   Ctrl+V: paste   commands joined with > make a queue",
            new Vector2(xPositionOnScreen + Pad, yPositionOnScreen - font.LineSpacing - 4), Color.White * 0.9f);
        drawMouse(b);
    }
}
