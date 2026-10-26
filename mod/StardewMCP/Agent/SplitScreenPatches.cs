using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Harmony patches for split-screen recording. They only act while <see cref="ModConfig.SplitScreen"/>
/// is on (and their own setting), so a single-player run plays exactly as before.
/// </summary>
public static class SplitScreenPatches
{
    private static ModConfig _config = null!;
    private static string? _pausedText;
    private static HashSet<string>? _hiddenInfoText;
    private static bool _inAddPlayer;

    /// <summary>Chat info keys hidden from every screen (debug cabins on smoke saves).</summary>
    private static readonly HashSet<string> HiddenChatKeys = new() { "BuildingMagicBuild" };

    /// <summary>Info lines the game adds straight to the chat box, hidden by their text.</summary>
    private static readonly string[] HiddenInfoStrings = { "Strings\\UI:Chat_StartingServer" };

    /// <summary>Set by the debug "capture" op: save the next composed frame (all screens) here as PNG.</summary>
    public static volatile string? PendingCapture;
    public static volatile string? LastCapture;

    private static bool Clean => _config.SplitScreen && _config.CleanFrames;

    /// <summary>The real devices reach only screen 0 (P1); blank them there once a save is loaded.</summary>
    private static bool HostLocked => _config.SplitScreen && _config.LockHostInput && Context.ScreenId == 0 && Context.IsWorldReady &&
                                      Players.Host.Role == SeatRole.Ai;

    public static void Apply(IManifest manifest, ModConfig config)
    {
        _config = config;
        var h = new Harmony(manifest.UniqueID);
        void Prefix(System.Reflection.MethodBase? m, string name) => h.Patch(m, prefix: new HarmonyMethod(typeof(SplitScreenPatches), name));
        void Postfix(System.Reflection.MethodBase? m, string name) => h.Patch(m, postfix: new HarmonyMethod(typeof(SplitScreenPatches), name));

        Postfix(AccessTools.Method(typeof(GameRunner), "Draw"), nameof(GameRunnerDraw_Postfix));
        h.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.SetWindowSize)),
            transpiler: new HarmonyMethod(typeof(SplitScreenPatches), nameof(WindowLayout_Transpiler)));

        // SMAPI reads the real devices through these base methods every tick.
        Postfix(AccessTools.Method(typeof(InputState), nameof(InputState.GetKeyboardState)), nameof(Keyboard_Postfix));
        Postfix(AccessTools.Method(typeof(InputState), nameof(InputState.GetMouseState)), nameof(Mouse_Postfix));
        Postfix(AccessTools.Method(typeof(InputState), nameof(InputState.GetGamePadState)), nameof(GamePad_Postfix));

        Prefix(AccessTools.Method(typeof(Game1), nameof(Game1.drawMouseCursor)), nameof(DrawCursor_Prefix));
        Prefix(AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.drawMouse)), nameof(DrawCursor_Prefix));
        Postfix(AccessTools.PropertyGetter(typeof(Game1), nameof(Game1.IsHudDrawn)), nameof(IsHudDrawn_Postfix));
        Prefix(AccessTools.Method(typeof(SpriteText), nameof(SpriteText.drawStringWithScrollBackground)), nameof(ScrollText_Prefix));
        h.Patch(AccessTools.Method(typeof(Multiplayer), nameof(Multiplayer.addPlayer)),
            prefix: new HarmonyMethod(typeof(SplitScreenPatches), nameof(AddPlayer_Prefix)),
            finalizer: new HarmonyMethod(typeof(SplitScreenPatches), nameof(AddPlayer_Finalizer)));
        Prefix(AccessTools.Method(typeof(Multiplayer), "receiveChatInfoMessage"), nameof(ChatInfo_Prefix));
        Prefix(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.addInfoMessage)), nameof(AddInfoMessage_Prefix));
    }

    // ------------------------------------------------------------------ input lock (P1)

    private static IEnumerable<CodeInstruction> WindowLayout_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var item = AccessTools.PropertyGetter(typeof(List<Vector4>), "Item");
        foreach (var instruction in instructions)
        {
            yield return instruction;
            // Change the normalized layout before the game allocates screen/UI render targets,
            // so every viewport has the correct aspect ratio rather than stretching a final image.
            if (instruction.Calls(item))
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SplitScreenPatches), nameof(DashboardWindow)));
        }
    }

    private static Vector4 DashboardWindow(Vector4 original)
    {
        if (!_config.OperatorDashboard || !_config.SplitScreen ||
            !Players.Slots.Any(s => s.Role == SeatRole.Camera && s.Screen >= Players.ScreenCount))
            return original;
        int count = GameRunner.instance.gameInstances.Count;
        int index = Game1.game1.instanceIndex;
        if ("bottom".Equals(_config.DashboardLayout, StringComparison.OrdinalIgnoreCase) && count == 2 && Players.ScreenCount == 2)
            return BottomDashboardBounds(index);
        if (count == 3 && Players.ScreenCount == 3)
            return index switch
            {
                0 => new Vector4(0f, 0f, .5f, .5f),
                1 => new Vector4(0f, .5f, .5f, .5f),
                2 => new Vector4(.5f, .5f, .5f, .5f),
                _ => original
            };
        return original;
    }

    internal static Vector4 BottomDashboardBounds(int index) => index switch
        {
            0 => new Vector4(0f, 0f, 0.5f, 0.6f),
            1 => new Vector4(0.5f, 0f, 0.5f, 0.6f),
            2 => new Vector4(0f, 0.6f, 1f, 0.4f),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

    private static void Keyboard_Postfix(ref KeyboardState __result)
    {
        if (HostLocked) __result = default;
    }

    private static void Mouse_Postfix(ref MouseState __result)
    {
        // Parked in the corner: no clicks, no hover tooltips.
        if (HostLocked)
            __result = new MouseState(0, 0, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }

    private static void GamePad_Postfix(ref GamePadState __result)
    {
        if (HostLocked) __result = default;
    }

    // ------------------------------------------------------------------ clean frames

    /// <summary>Skip the "Paused" scroll drawn on every screen while the world is frozen.</summary>
    private static bool ScrollText_Prefix(string s)
    {
        if ((!Clean && !_config.OperatorDashboard) || !Context.IsWorldReady || !Game1.HostPaused)
            return true;
        _pausedText ??= Game1.content.LoadString("Strings\\StringsFromCSFiles:DayTimeMoneyBox.cs.10378");
        return s != _pausedText;
    }

    /// <summary>The game hides the whole HUD while paused; keep it so frozen frames match live ones.</summary>
    private static void IsHudDrawn_Postfix(ref bool __result)
    {
        if (__result || (!Clean && !_config.OperatorDashboard) || !Game1.HostPaused)
            return;
        __result = (Game1.displayHUD || Game1.eventUp) && Game1.gameMode == 3 && !Game1.freezeControls && !Game1.panMode && !Game1.game1.takingMapScreenshot;
    }

    /// <summary>Nobody uses a mouse: hide P1's cursor and the farmhands' snapped gamepad cursors.</summary>
    private static bool DrawCursor_Prefix() => !Clean;

    private static void AddPlayer_Prefix() => _inAddPlayer = true;

    private static Exception? AddPlayer_Finalizer(Exception? __exception)
    {
        _inAddPlayer = false;
        return __exception;
    }

    /// <summary>"X (127.0.0.1) has joined" is added inside Multiplayer.addPlayer; "now hosting" by the server.</summary>
    private static bool AddInfoMessage_Prefix(string message)
    {
        if (!Clean)
            return true;
        if (_inAddPlayer)
            return false;
        _hiddenInfoText ??= HiddenInfoStrings.Select(k => Game1.content.LoadString(k)).ToHashSet();
        return !_hiddenInfoText.Contains(message);
    }

    private static bool ChatInfo_Prefix(string messageKey) => !(Clean && HiddenChatKeys.Contains(messageKey));

    // ------------------------------------------------------------------ frame capture (debug)

    /// <summary>After every screen is composed into the back buffer, save it if a capture was asked for.</summary>
    private static void GameRunnerDraw_Postfix()
    {
        OperatorPanel.Draw();
        var path = PendingCapture;
        if (path == null)
            return;
        PendingCapture = null;
        try
        {
            var gd = Game1.graphics.GraphicsDevice;
            int w = gd.PresentationParameters.BackBufferWidth, h = gd.PresentationParameters.BackBufferHeight;
            var data = new Color[w * h];
            gd.GetBackBufferData(data);
            for (int i = 0; i < data.Length; i++)
                data[i].A = 255;
            using var tex = new Texture2D(gd, w, h);
            tex.SetData(data);
            using var fs = File.Create(path);
            tex.SaveAsPng(fs, w, h);
            LastCapture = $"{path} ({w}x{h})";
        }
        catch (Exception ex)
        {
            LastCapture = "error: " + ex.Message;
        }
    }
}
