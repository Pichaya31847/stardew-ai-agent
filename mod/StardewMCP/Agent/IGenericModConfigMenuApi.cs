using System;
using StardewModdingAPI;

namespace StardewMCP.Agent;

/// <summary>
/// The part of Generic Mod Config Menu's API this mod uses (an optional dependency: the menu appears when GMCM is
/// installed, config.json works without it). The interface is published by GMCM for mods to copy:
/// https://github.com/spacechase0/StardewValleyMods/blob/develop/GenericModConfigMenu/IGenericModConfigMenuApi.cs
/// </summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);
    void AddParagraph(IManifest mod, Func<string> text);

    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
    void AddTextOption(IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name, Func<string>? tooltip = null, string[]? allowedValues = null, Func<string, string>? formatAllowedValue = null, string? fieldId = null);

    void AddPage(IManifest mod, string pageId, Func<string>? pageTitle = null);
    void AddPageLink(IManifest mod, string pageId, Func<string> text, Func<string>? tooltip = null);
}
