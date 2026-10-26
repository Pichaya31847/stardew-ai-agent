using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

/// <summary>The argument and schema helpers every tool uses (AgentTools.S, I, Has, TileList, Reason, Obj...).</summary>
public class ToolArgsTests
{
    /// <summary>JSON written with single quotes so the tests stay readable.</summary>
    private static JsonElement Json(string text) => JsonDocument.Parse(text.Replace('\'', '"')).RootElement.Clone();

    // ---------------------------------------------------------------- S and I

    [Fact]
    public void S_reads_a_string_argument()
    {
        Assert.Equal("Axe", AgentTools.S(Json("{'tool':'Axe'}"), "tool"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{'tool':null}")]
    [InlineData("{'tool':5}")]
    [InlineData("{'tool':['Axe']}")]
    [InlineData("[]")]
    [InlineData("'Axe'")]
    public void S_falls_back_when_the_argument_is_missing_or_not_a_string(string args)
    {
        Assert.Equal("", AgentTools.S(Json(args), "tool"));
        Assert.Equal("none", AgentTools.S(Json(args), "tool", "none"));
    }

    [Fact]
    public void I_reads_a_number_argument()
    {
        Assert.Equal(12, AgentTools.I(Json("{'radius':12}"), "radius", 5));
        Assert.Equal(-3, AgentTools.I(Json("{'x':-3}"), "x", 0));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{'radius':null}")]
    [InlineData("{'radius':'twelve'}")]
    [InlineData("{'radius':true}")]
    [InlineData("[]")]
    public void I_falls_back_when_the_argument_is_missing_or_not_a_number(string args)
    {
        Assert.Equal(7, AgentTools.I(Json(args), "radius", 7));
    }

    [Theory]
    [InlineData("{'radius':'12'}")]
    [InlineData("{'radius':12.0}")]
    [InlineData("{'radius':' 12 '}")]
    public void I_accepts_numeric_strings_and_whole_decimals_as_small_models_send_them(string args)
    {
        Assert.Equal(12, AgentTools.I(Json(args), "radius", 7));
    }

    // ---------------------------------------------------------------- Has

    [Fact]
    public void Has_is_true_for_present_values_even_when_zero_or_empty()
    {
        var args = Json("{'x':0,'name':'','flag':false}");
        Assert.True(AgentTools.Has(args, "x"));
        Assert.True(AgentTools.Has(args, "name"));
        Assert.True(AgentTools.Has(args, "flag"));
    }

    [Fact]
    public void Has_is_false_for_missing_and_null_values()
    {
        var args = Json("{'x':null}");
        Assert.False(AgentTools.Has(args, "x"));
        Assert.False(AgentTools.Has(args, "y"));
        Assert.False(AgentTools.Has(Json("[]"), "x"));
    }

    // ---------------------------------------------------------------- TileList

    [Fact]
    public void TileList_reads_x_y_pairs_in_order()
    {
        var tiles = AgentTools.TileList(Json("{'tiles':[[1,2],[3,4],[0,0]]}"), "tiles");
        Assert.Equal(new List<Point> { new Point(1, 2), new Point(3, 4), new Point(0, 0) }, tiles);
    }

    [Fact]
    public void TileList_skips_entries_that_are_not_pairs_and_ignores_extra_numbers()
    {
        var tiles = AgentTools.TileList(Json("{'tiles':[[1,2],[3],'5,6',7,[8,9,10],[]]}"), "tiles");
        Assert.Equal(new List<Point> { new Point(1, 2), new Point(5, 6), new Point(8, 9) }, tiles);
    }

    [Theory]
    [InlineData("{'tiles':'[(12, 29), (13, 29)]'}")]   // the whole list as one string (local Qwen run)
    [InlineData("{'tiles':'12,29 13,29'}")]
    [InlineData("{'tiles':[{'x':12,'y':29},{'x':'13','y':29.0}]}")]
    [InlineData("{'tiles':['(12, 29)','13,29']}")]
    [InlineData("{'tiles':[12,29,13,29]}")]             // flat numbers, paired up
    [InlineData("{'tiles':[['12','29'],[13,29]]}")]
    public void TileList_reads_the_shapes_small_models_send(string args)
    {
        Assert.Equal(new List<Point> { new Point(12, 29), new Point(13, 29) }, AgentTools.TileList(Json(args), "tiles"));
    }

    [Theory]
    [InlineData("{'tile':[4,5]}")]
    [InlineData("{'tile':'4,5'}")]
    [InlineData("{'x':4,'y':5}")]
    public void TileList_of_tiles_also_takes_one_tile_or_x_and_y(string args)
    {
        Assert.Equal(new List<Point> { new Point(4, 5) }, AgentTools.TileList(Json(args), "tiles"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{'tiles':null}")]
    [InlineData("{'tiles':'no numbers here'}")]
    [InlineData("{'tiles':{'x':1}}")]
    [InlineData("{'tiles':[7]}")]
    [InlineData("[]")]
    public void TileList_is_empty_when_the_argument_is_missing_or_not_a_list(string args)
    {
        Assert.Empty(AgentTools.TileList(Json(args), "tiles"));
    }

    // ---------------------------------------------------------------- TextArg

    [Theory]
    [InlineData("{'text':'plan the day'}")]
    [InlineData("{'thought':'plan the day'}")]
    [InlineData("{'reason':'plan the day'}")]
    [InlineData("{'text':'  ','content':'plan the day','reason':'other'}")]
    public void TextArg_falls_back_to_another_string_argument(string args)
    {
        Assert.Equal("plan the day", AgentTools.TextArg(Json(args), "text"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{'text':''}")]
    [InlineData("{'n':3}")]
    public void TextArg_is_empty_when_no_string_was_given(string args)
    {
        Assert.Equal("", AgentTools.TextArg(Json(args), "text"));
    }

    // ---------------------------------------------------------------- Reason and ArgsForLog

    [Fact]
    public void Reason_returns_the_reason_text_or_an_empty_string()
    {
        Assert.Equal("clear the field", AgentTools.Reason(Json("{'reason':'clear the field','x':1}")));
        Assert.Equal("", AgentTools.Reason(Json("{'x':1}")));
        Assert.Equal("", AgentTools.Reason(Json("{'reason':3}")));
        Assert.Equal("", AgentTools.Reason(Json("[]")));
    }

    [Fact]
    public void ArgsForLog_leaves_out_the_reason()
    {
        var logged = Assert.IsType<Dictionary<string, JsonElement>>(AgentTools.ArgsForLog(Json("{'x':3,'y':4,'reason':'walk'}")));
        Assert.Equal(new List<string> { "x", "y" }, logged.Keys.ToList());
        Assert.Equal(3, logged["x"].GetInt32());
    }

    [Fact]
    public void ArgsForLog_is_null_when_there_is_nothing_but_a_reason()
    {
        Assert.Null(AgentTools.ArgsForLog(Json("{'reason':'look around'}")));
        Assert.Null(AgentTools.ArgsForLog(Json("{}")));
        Assert.Null(AgentTools.ArgsForLog(Json("[1]")));
    }

    // ---------------------------------------------------------------- schema builders

    [Fact]
    public void Str_and_Int_build_typed_properties_with_a_description()
    {
        var str = JsonSerializer.SerializeToElement(AgentTools.Str("Item name"));
        Assert.Equal("string", str.GetProperty("type").GetString());
        Assert.Equal("Item name", str.GetProperty("description").GetString());

        var num = JsonSerializer.SerializeToElement(AgentTools.Int("Tile x"));
        Assert.Equal("integer", num.GetProperty("type").GetString());
        Assert.Equal("Tile x", num.GetProperty("description").GetString());
    }

    [Fact]
    public void Tiles_builds_an_array_of_integer_pairs()
    {
        var tiles = JsonSerializer.SerializeToElement(AgentTools.Tiles("List of [x,y] tiles"));
        Assert.Equal("array", tiles.GetProperty("type").GetString());
        var item = tiles.GetProperty("items");
        Assert.Equal("array", item.GetProperty("type").GetString());
        Assert.Equal(2, item.GetProperty("minItems").GetInt32());
        Assert.Equal(2, item.GetProperty("maxItems").GetInt32());
        Assert.Equal("integer", item.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void Obj_builds_an_object_schema_with_its_properties_and_required_names()
    {
        var schema = JsonSerializer.SerializeToElement(
            AgentTools.Obj(new { x = AgentTools.Int("Tile x"), reason = AgentTools.Str("Why") }, "x", "reason"));
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("x", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("reason", out _));
        var required = schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToList();
        Assert.Equal(new List<string> { "x", "reason" }, required);
    }

    [Fact]
    public void Obj_without_required_names_gives_an_empty_required_list()
    {
        var schema = JsonSerializer.SerializeToElement(AgentTools.Obj(new { }));
        Assert.Equal(JsonValueKind.Array, schema.GetProperty("required").ValueKind);
        Assert.Equal(0, schema.GetProperty("required").GetArrayLength());
    }
}
