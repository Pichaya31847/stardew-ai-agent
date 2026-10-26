using System.Runtime.CompilerServices;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>Fill each new-farm form once, leaving it editable and waiting for the player's OK.</summary>
internal static class NewGamePrefill
{
    private static readonly ConditionalWeakTable<CharacterCustomization, object> Filled = new();

    internal static void Tick(IModHelper helper, ModConfig config)
    {
        if (!config.PrefillNewGame || Context.IsWorldReady)
            return;
        var menu = Game1.activeClickableMenu is TitleMenu ? TitleMenu.subMenu : Game1.activeClickableMenu;
        if (menu is not CharacterCustomization cc ||
            cc.source is not (CharacterCustomization.Source.NewGame or CharacterCustomization.Source.HostNewFarm) ||
            Filled.TryGetValue(cc, out _))
            return;

        helper.Reflection.GetField<TextBox>(cc, "nameBox").GetValue().Text = config.NewGamePlayerName;
        helper.Reflection.GetField<TextBox>(cc, "farmnameBox").GetValue().Text = config.NewGameFarmName;
        helper.Reflection.GetField<TextBox>(cc, "favThingBox").GetValue().Text = config.NewGameFavoriteThing;
        helper.Reflection.GetField<bool>(cc, "skipIntro").SetValue(config.NewGameSkipIntro);
        cc.skipIntroButton.sourceRect.X = config.NewGameSkipIntro ? 236 : 227;
        Filled.Add(cc, new object());
    }
}
