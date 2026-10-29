using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>Composed after the game views; a camera region becomes a local operator console.</summary>
public static class OperatorPanel
{
    private static ModConfig _cfg = null!;
    private static IModHelper _helper = null!;
    private static readonly object Gate = new();
    private static readonly Queue<(string text, Color color)> Feed = new();
    private static readonly Dictionary<string, string> Thoughts = new();
    private static readonly Dictionary<string, string> Speech = new();
    private static readonly List<(Rectangle bounds, string target)> Targets = new();
    private static Rectangle _panel, _inputBounds, _sendBounds;
    private static TextBox? _input;
    private static KeyboardDispatcher? _dispatcher;
    private static IKeyboardSubscriber? _previous;
    private static SpriteBatch? _batch;
    private static readonly RasterizerState PanelRasterizer = new() { ScissorTestEnable = true };
    private static string _target = "all";
    private static string _notice = "Click the command box or press F9. Enter sends; Esc releases keyboard.";
    public static bool Typing { get; private set; }

    public static void Init(IModHelper helper, ModConfig cfg)
    {
        _cfg = cfg;
        _helper = helper;
        AgentLog.Added += OnEntry;
        helper.Events.Input.ButtonPressed += OnButton;
        helper.Events.Input.ButtonsChanged += (_, e) =>
        {
            if (Context.ScreenId == 0 && Typing)
                foreach (var button in e.Held.Concat(e.Pressed)) helper.Input.Suppress(button);
        };
        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            if (Context.ScreenId != 0) return;
            _dispatcher = Game1.keyboardDispatcher;
            if (!_cfg.OperatorDashboard || !Context.IsWorldReady) { Blur(); return; }
            if (Typing && _input != null && _dispatcher.Subscriber != _input)
                _dispatcher.Subscriber = _input;
        };
        helper.Events.GameLoop.SaveLoaded += (_, _) =>
        {
            // Preserve seat identities when adding the dashboard to an existing farm: rename on each seat's load.
            if (!_cfg.OperatorDashboard || !Context.IsWorldReady) return;
            var desired = _cfg.Seat(Context.ScreenId).Name;
            if (!string.IsNullOrWhiteSpace(desired))
            {
                Game1.player.Name = desired;
                Game1.player.displayName = desired;
            }
        };
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            if (Context.ScreenId != 0) return;
            Blur();
            lock (Gate) { Feed.Clear(); Thoughts.Clear(); Speech.Clear(); }
        };
        helper.ConsoleCommands.Add("mcp_order", "mcp_order all|AI1|AI2|AI3 instruction", (_, args) =>
        {
            if (args.Length >= 2) Send(args[0], string.Join(" ", args.Skip(1)));
        });
    }

    private static string Name(string id) => Players.ById(id) is { } s
        ? ((s.State as ScreenState)?.Name ?? _cfg.Seat(s.Screen).Name ?? id) : id;

    private static void OnEntry(LogEntry entry)
    {
        if (!_cfg.OperatorDashboard) return;
        lock (Gate)
        {
            if (entry.Player != null && !string.IsNullOrWhiteSpace(entry.Text))
            {
                if (entry.Kind == "think") Thoughts[entry.Player] = entry.Text;
                if (entry.Kind == "say") Speech[entry.Player] = entry.Text;
            }
            if (entry.Kind != "say" && entry.Kind != "action" && entry.Kind != "system" && entry.Ok != false) return;
            string text = entry.Text ?? entry.Tool ?? "";
            if (string.IsNullOrWhiteSpace(text)) return;
            Feed.Enqueue(($"{entry.GameTime} {Name(entry.Player ?? "")} [{entry.Kind}] {text}",
                entry.Ok == false ? Color.Salmon : entry.Kind == "say" ? Color.LightGreen : Color.LightGray));
            while (Feed.Count > 100) Feed.Dequeue();
        }
    }

    private static void Focus()
    {
        // The custom panel draws its own input. Native TextBox defaults to Width=0 and
        // limitWidth=true, which otherwise truncates every newly typed character.
        _input ??= CreateInput(Game1.smallFont);
        if (_dispatcher == null) return;
        if (!Typing) _previous = _dispatcher.Subscriber;
        Typing = true;
        _dispatcher.Subscriber = _input;
    }

    internal static TextBox CreateInput(SpriteFont font) =>
        new(null, null, font, Color.White) { limitWidth = false, Text = "", textLimit = 1000 };

    private static void Blur()
    {
        if (Typing && _dispatcher != null && _dispatcher.Subscriber == _input) _dispatcher.Subscriber = _previous;
        Typing = false;
    }

    private static void OnButton(object? sender, ButtonPressedEventArgs e)
    {
        if (!_cfg.OperatorDashboard || Context.ScreenId != 0 || !Context.IsWorldReady) return;
        if (e.Button == SButton.F9) { Focus(); _helper.Input.Suppress(e.Button); return; }
        if (Typing && e.Button == SButton.Escape) { Blur(); _helper.Input.Suppress(e.Button); return; }
        if (Typing && e.Button == SButton.Enter) { Submit(); _helper.Input.Suppress(e.Button); return; }
        if (e.Button == SButton.MouseLeft)
        {
            var mouse = Mouse.GetState();
            var point = new Point(mouse.X, mouse.Y);
            if (_sendBounds.Contains(point)) Submit();
            else if (_inputBounds.Contains(point)) Focus();
            else
            {
                var target = Targets.FirstOrDefault(t => t.bounds.Contains(point));
                if (target.target != null) _target = target.target;
                else Blur();
            }
            if (_panel.Contains(point)) _helper.Input.Suppress(e.Button);
        }
        else if (Typing) _helper.Input.Suppress(e.Button);
    }

    private static void Submit()
    {
        if (_input == null || string.IsNullOrWhiteSpace(_input.Text)) return;
        Send(_target, _input.Text.Trim());
        _input.Text = "";
    }

    public static int Send(string target, string text)
    {
        if (!_cfg.OperatorDashboard || string.IsNullOrWhiteSpace(text) || text.Length > 1000) return 0;
        int count = 0;
        foreach (var seat in Players.Slots.Where(s => s.Role == SeatRole.Ai))
        {
            if (target != "all" && !Name(seat.Id).Equals(target, StringComparison.OrdinalIgnoreCase) && seat.Id != target) continue;
            var brain = Brain.Brains.For(seat.Id);
            if (brain == null) continue;
            brain.Order(text);
            seat.Runtime.Log.Add("system", text: "Operator instruction: " + text);
            count++;
        }
        _notice = count == 0 ? "No matching AI is running." : $"Queued for {count} AI(s); applied before the next model request.";
        return count;
    }

    public static void Draw()
    {
        if (!_cfg.OperatorDashboard || GameRunner.instance?.gameInstances.Count < 2) return;
        var camera = Players.Slots.FirstOrDefault(s => s.Role == SeatRole.Camera);
        var instances = GameRunner.instance?.gameInstances;
        if (camera == null || instances == null) return;
        if (camera.Screen >= Players.ScreenCount)
        {
            // Reserve pixels without creating a farmhand, cabin, input target or sleep participant.
            if (instances.Count != Players.ScreenCount) return;
            var presentation = Game1.graphics.GraphicsDevice.PresentationParameters;
            int width = presentation.BackBufferWidth, height = presentation.BackBufferHeight;
            _panel = "bottom".Equals(_cfg.DashboardLayout, StringComparison.OrdinalIgnoreCase) && Players.ScreenCount == 2
                ? new Rectangle(0, (int)(height * .6f), width, height - (int)(height * .6f))
                : new Rectangle(width / 2, 0, width - width / 2, height / 2);
        }
        else
        {
            if (camera.Screen >= instances.Count) return;
            _panel = instances[camera.Screen].localMultiplayerWindow;
        }
        // Validated SMAPI ticks can stop during saving; the operator UI must remain visible.
        if (_panel.Width < 100 || _panel.Height < 100 || Game1.gameMode != 3) return;
        var device = Game1.graphics.GraphicsDevice;
        var oldViewport = device.Viewport;
        var oldScissor = device.ScissorRectangle;
        var oldRasterizer = device.RasterizerState;
        device.Viewport = new Viewport(0, 0, device.PresentationParameters.BackBufferWidth, device.PresentationParameters.BackBufferHeight);
        device.ScissorRectangle = Rectangle.Intersect(_panel, device.Viewport.Bounds);
        var batch = _batch ??= new SpriteBatch(device);
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, PanelRasterizer);
        try
        {
            Color ink = new(75, 49, 31), muted = new(134, 103, 72), cream = new(250, 237, 202);
            Color green = new(73, 105, 66), bark = new(99, 57, 31), honey = new(190, 132, 66);
            var font = Game1.smallFont;
            float scale = Math.Clamp(_panel.Width / 1280f, 0.5f, 0.8f);
            int line = (int)Math.Ceiling(font.LineSpacing * scale);
            int pad = Math.Max(12, _panel.Width / 60);
            void Fill(Rectangle r, Color c)
            {
                var clipped = Rectangle.Intersect(r, _panel);
                if (clipped.Width > 0 && clipped.Height > 0) batch.Draw(Game1.fadeToBlackRect, clipped, c);
            }
            void Box(Rectangle r, Color face, Color edge)
            {
                Fill(new Rectangle(r.X + 3, r.Y + 4, r.Width, r.Height), bark * 0.32f);
                Fill(r, edge);
                Fill(new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), face);
                Fill(new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, 2), Color.White * 0.23f);
            }
            int Text(string text, int x, int y, int width, Color color, int max = 1, float size = 1)
            {
                int used = 0;
                foreach (var part in Hud.Wrap(font, Hud.Sanitize(font, text), Math.Max(20, (int)(width / (scale * size)) - 12), max))
                {
                    batch.DrawString(font, part, new Vector2(x, y + used * line), color, 0, Vector2.Zero, scale * size, SpriteEffects.None, 0);
                    used++;
                }
                return used * line;
            }
            // Small, deterministic plank grooves: no external image or texture dependency.
            Fill(_panel, bark);
            for (int yy = _panel.Y + 3; yy < _panel.Bottom; yy += 38)
            {
                Fill(new Rectangle(_panel.X + 3, yy, _panel.Width - 6, 35), honey);
                Fill(new Rectangle(_panel.X + 8, yy + 4, _panel.Width - 16, 1), new Color(222, 168, 93));
                for (int xx = _panel.X + 20 + (yy % 31); xx < _panel.Right - 20; xx += 91)
                    Fill(new Rectangle(xx, yy + 21, Math.Min(47, _panel.Right - xx - 8), 1), new Color(167, 109, 54));
            }
            var paper = new Rectangle(_panel.X + 7, _panel.Y + 7, _panel.Width - 14, _panel.Height - 14);
            Box(paper, cream, new Color(135, 85, 44));
            int left = _panel.X + pad + 7, width = _panel.Width - 2 * pad - 14;
            int top = _panel.Y + pad + 5;
            // Pixel sprout motif.
            Fill(new Rectangle(left + 10, top + 12, 3, 20), green);
            Fill(new Rectangle(left + 1, top + 9, 10, 7), new Color(119, 151, 75));
            Fill(new Rectangle(left + 12, top + 4, 12, 8), green);
            Text("FARMHOUSE", left + 35, top, width - 230, ink, 1, 1.15f);
            int farmerCount = Players.Slots.Count(s => s.Role == SeatRole.Ai);
            Text($"{farmerCount} farmers, one little valley", left + 35, top + line + 5, width - 230, muted);
            var badge = new Rectangle(left + width - 155, top + 2, 155, line + 12);
            Box(badge, RunControl.Holding ? new Color(228, 196, 126) : green, bark);
            Text(RunControl.Holding ? "PAUSED  /  F8" : "LIVE  /  F8", badge.X + 12, badge.Y + 5, badge.Width - 16,
                RunControl.Holding ? ink : cream);
            int cardsTop = top + 2 * line + 19;
            int footerH = 3 * line + 58;
            int footerTop = _panel.Bottom - pad - footerH;
            int gap = 10;
            bool wide = "bottom".Equals(_cfg.DashboardLayout, StringComparison.OrdinalIgnoreCase);
            int cardsWidth = wide ? width * 2 / 3 - gap : width;
            int cardWidth = (cardsWidth - (farmerCount - 1) * gap) / Math.Max(1, farmerCount);
            int cardHeight = wide ? Math.Max(80, footerTop - cardsTop - 15)
                : Math.Min(190, Math.Max(102, (footerTop - cardsTop) * 2 / 3));
            var accents = new[] { new Color(83, 115, 70), new Color(163, 110, 65), new Color(75, 124, 133) };
            lock (Gate)
            {
                int index = 0;
                foreach (var seat in Players.Slots.Where(s => s.Role == SeatRole.Ai).Take(3))
                {
                    int x = left + index * (cardWidth + gap);
                    var card = new Rectangle(x, cardsTop, cardWidth, cardHeight);
                    Box(card, new Color(255, 247, 222), new Color(200, 164, 110));
                    Fill(new Rectangle(x + 2, cardsTop + 2, cardWidth - 4, line + 18), accents[index]);
                    Text(Name(seat.Id), x + 12, cardsTop + 7, cardWidth - 24, Color.White);
                    int y = cardsTop + line + 28;
                    int innerWidth = cardWidth - 24;
                    var brain = Brain.Brains.For(seat.Id);
                    string status = seat.Runtime.CurrentTool ?? brain?.Status ?? "waiting";
                    Text(status, x + 12, y, innerWidth, ink); y += line;
                    var state = seat.State as ScreenState;
                    Text($"{state?.Time}  {state?.Location}", x + 12, y, innerWidth, muted); y += line + 5;
                    int remaining = Math.Max(0, (card.Bottom - 8 - y) / line);
                    if (remaining > 0 && Thoughts.TryGetValue(seat.Id, out var thought))
                        y += Text(thought, x + 12, y, innerWidth, ink, Math.Min(remaining, 2));
                    remaining = Math.Max(0, (card.Bottom - 8 - y) / line);
                    if (remaining > 0 && Speech.TryGetValue(seat.Id, out var speech))
                        Text("Say: " + speech, x + 12, y, innerWidth, green, remaining);
                    index++;
                }
                int logTop = wide ? cardsTop : cardsTop + cardHeight + 13;
                int logLeft = wide ? left + cardsWidth + gap * 2 : left + 2;
                int logWidth = wide ? width - cardsWidth - gap * 2 : width - 4;
                int room = Math.Max(0, (footerTop - logTop - 12) / line - 1);
                Text("FIELD NOTES", logLeft, logTop, logWidth, muted);
                if (room > 0)
                {
                    int y = logTop + line + 3;
                    foreach (var entry in Feed.Reverse().Take(room).Reverse())
                    {
                        Text(entry.text, logLeft, y, logWidth, entry.color == Color.Salmon ? new Color(160, 67, 48) : ink);
                        y += line;
                    }
                }
            }
            Fill(new Rectangle(left, footerTop - 7, width, 1), new Color(210, 180, 132));
            Targets.Clear();
            int targetX = left + 85;
            Text("TO", left + 3, footerTop + 7, 65, muted);
            foreach (var label in new[] { "all" }.Concat(Players.Slots.Where(s => s.Role == SeatRole.Ai).Select(s => Name(s.Id))))
            {
                int buttonWidth = Math.Min(92, (width - 105) / (farmerCount + 1));
                var button = new Rectangle(targetX, footerTop, buttonWidth, line + 13);
                Targets.Add((button, label));
                bool selected = label == _target;
                Box(button, selected ? green : new Color(238, 218, 177), selected ? new Color(49, 77, 43) : new Color(188, 149, 96));
                Text(label == "all" ? "ALL" : label, targetX + 8, footerTop + 5, buttonWidth - 12, selected ? Color.White : ink);
                targetX += buttonWidth + 8;
            }
            int inputY = footerTop + line + 22;
            _inputBounds = new Rectangle(left, inputY, width - 100, line + 21);
            _sendBounds = new Rectangle(_inputBounds.Right + 9, inputY, 91, line + 21);
            Box(_inputBounds, Typing ? new Color(255, 252, 238) : new Color(249, 238, 211), Typing ? green : new Color(177, 136, 85));
            Box(_sendBounds, green, new Color(48, 77, 43));
            string inputText = _input?.Text ?? "";
            while (inputText.Length > 0 && font.MeasureString(Hud.Sanitize(font, inputText)).X * scale > _inputBounds.Width - 24)
                inputText = inputText.Substring(1);
            Text(inputText.Length == 0 ? (Typing ? "|" : "A little plan for the farm...   F9 to write") : inputText + (Typing ? "|" : ""),
                left + 10, inputY + 9, _inputBounds.Width - 20, inputText.Length == 0 && !Typing ? muted : ink);
            Text("SEND", _sendBounds.X + 15, inputY + 9, 70, Color.White);
            Text(_notice, left + 2, _inputBounds.Bottom + 8, width - 4, muted);
            var mouse = Mouse.GetState();
            if (_panel.Contains(mouse.X, mouse.Y))
            {
                Fill(new Rectangle(mouse.X + 1, mouse.Y + 1, 3, 14), bark);
                Fill(new Rectangle(mouse.X, mouse.Y, 2, 12), cream);
                Fill(new Rectangle(mouse.X, mouse.Y, 10, 2), cream);
            }
        }
        finally
        {
            batch.End();
            device.Viewport = oldViewport;
            device.ScissorRectangle = oldScissor;
            device.RasterizerState = oldRasterizer;
        }
    }
}

