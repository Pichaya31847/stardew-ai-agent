using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;

namespace StardewMCP.Agent;

// Argument and schema helpers for the tools, moved here from AgentTools.cs (Tiles, Has and TileList went
// from private to internal so the tests can call them). They only use System.Text.Json and Point, so the
// unit tests (mod/StardewMCP.Tests) compile this file as is.

public static partial class AgentTools
{
    // ------------------------------------------------------------------ schema helpers

    internal static object Str(string desc) => new { type = "string", description = desc };
    internal static object Int(string desc) => new { type = "integer", description = desc };
    internal static object Tiles(string desc) => new
    {
        type = "array",
        description = desc,
        items = new { type = "array", items = new { type = "integer" }, minItems = 2, maxItems = 2 }
    };
    internal static object Obj(object props, params string[] required) => new { type = "object", properties = props, required };

    // ------------------------------------------------------------------ argument helpers

    public static string Reason(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";

    public static object? ArgsForLog(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, JsonElement>();
        foreach (var p in args.EnumerateObject())
            if (p.Name != "reason") d[p.Name] = p.Value.Clone();
        return d.Count > 0 ? d : null;
    }

    internal static string S(JsonElement a, string name, string def = "") =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;

    internal static int I(JsonElement a, string name, int def) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && AsInt(v) is int n ? n : def;

    /// <summary>An integer from a JSON number (12 or 12.0) or a numeric string ("12"), as small models send them; else null.</summary>
    internal static int? AsInt(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt32(out var i) ? i : v.TryGetDouble(out var d) && d is >= int.MinValue and <= int.MaxValue ? (int)Math.Round(d) : null;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s is >= int.MinValue and <= int.MaxValue)
            return (int)Math.Round(s);
        return null;
    }

    internal static bool Has(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static readonly Regex Pair = new(@"(-?\d+)\s*[,\s]\s*(-?\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Tiles from [[x,y],...] and the shapes small models send instead (the local Qwen run lost 34 use_tool calls to
    /// "No tiles given"): [{"x":1,"y":2}], ["1,2", "(3, 4)"] and the whole list as one string "[(1, 2), (3, 4)]".
    /// </summary>
    internal static List<Point> TileList(JsonElement a, string name)
    {
        var list = TileListOf(a, name);
        // "tiles" also takes one tile as "tile" or as x and y.
        if (list.Count == 0 && name == "tiles")
        {
            list = TileListOf(a, "tile");
            if (list.Count == 0 && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("x", out var x) && a.TryGetProperty("y", out var y) && AsInt(x) is int px && AsInt(y) is int py)
                list.Add(new Point(px, py));
        }
        return list;
    }

    private static List<Point> TileListOf(JsonElement a, string name)
    {
        var list = new List<Point>();
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var arr)) return list;
        if (arr.ValueKind == JsonValueKind.String)
        {
            AddPairs(arr.GetString(), list);
            return list;
        }
        if (arr.ValueKind != JsonValueKind.Array) return list;
        // A flat list of numbers, [x1, y1, x2, y2...] (one tile is often sent as just [x, y]).
        if (arr.GetArrayLength() >= 2 && arr.GetArrayLength() % 2 == 0 && arr.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number))
        {
            for (int i = 0; i + 1 < arr.GetArrayLength(); i += 2)
                if (AsInt(arr[i]) is int fx && AsInt(arr[i + 1]) is int fy)
                    list.Add(new Point(fx, fy));
            return list;
        }
        foreach (var t in arr.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 2 && AsInt(t[0]) is int x && AsInt(t[1]) is int y)
                list.Add(new Point(x, y));
            else if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("x", out var tx) && t.TryGetProperty("y", out var ty) && AsInt(tx) is int ox && AsInt(ty) is int oy)
                list.Add(new Point(ox, oy));
            else if (t.ValueKind == JsonValueKind.String)
                AddPairs(t.GetString(), list);
        }
        return list;
    }

    private static void AddPairs(string? text, List<Point> list)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (Match m in Pair.Matches(text))
            list.Add(new Point(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// A required text argument, or (when a small model put it under another key) the first other string argument
    /// except reason: think lost all 26 of its calls in the local Qwen run to "text is empty".
    /// </summary>
    internal static string TextArg(JsonElement a, string name)
    {
        var text = S(a, name).Trim();
        if (text.Length > 0 || a.ValueKind != JsonValueKind.Object) return text;
        foreach (var p in a.EnumerateObject())
            if (p.Name != "reason" && p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString() ?? "").Trim() is { Length: > 0 } other)
                return other;
        return Reason(a).Trim();
    }
}
