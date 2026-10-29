using System.Linq;
using System.Text.Json;
using StardewMCP.Agent.Brain;
using Xunit;

namespace StardewMCP.Tests;

/// <summary>BrainTools: "full" sends the tools as they are, "compact" shortens all but the everyday ones.</summary>
public class ToolProfileTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text.Replace('\'', '"')).RootElement.Clone();

    private static readonly LlmTool Joja = new("joja", "Joja membership and projects. Long explanation follows. More.", Json(
        "{'type':'object','properties':{'action':{'type':'string','enum':['status','buy'],'description':'What to do'}," +
        "'description':{'type':'string','description':'an argument called description'}," +
        "'tiles':{'type':'array','description':'Tiles','items':{'type':'array','items':{'type':'integer'},'description':'x,y'}}," +
        "'reason':{'type':'string','description':'A long reason text'}},'required':['action','reason']}"));

    private static readonly LlmTool Look = new("look", "ASCII map. With details.", Json(
        "{'type':'object','properties':{'radius':{'type':'integer','description':'Tiles around you'},'reason':{'type':'string','description':'A long reason text'}},'required':[]}"));

    [Fact]
    public void Full_and_unknown_profiles_leave_the_tools_alone()
    {
        foreach (var profile in new[] { "full", "", null, "tiny" })
        {
            var tools = ToolProfile.Apply(profile, new[] { Joja, Look });
            Assert.Equal(Joja.Description, tools[0].Description);
            Assert.Equal(Joja.Schema.GetRawText(), tools[0].Schema.GetRawText());
        }
    }

    [Fact]
    public void Compact_cuts_other_tools_to_one_sentence_and_bare_arguments()
    {
        var joja = ToolProfile.Apply("Compact", new[] { Joja }).Single();
        Assert.Equal("Joja membership and projects.", joja.Description);
        var props = joja.Schema.GetProperty("properties");
        Assert.False(props.GetProperty("action").TryGetProperty("description", out _));
        Assert.Equal(2, props.GetProperty("action").GetProperty("enum").GetArrayLength());
        Assert.True(props.TryGetProperty("description", out var arg)); // an argument named "description" stays
        Assert.Equal("string", arg.GetProperty("type").GetString());
        Assert.False(props.GetProperty("tiles").GetProperty("items").TryGetProperty("description", out _));
        Assert.Equal(new[] { "action", "reason" }, joja.Schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public void Compact_keeps_core_tools_whole_but_shortens_their_reason()
    {
        var look = ToolProfile.Apply("compact", new[] { Look }).Single();
        Assert.Equal(Look.Description, look.Description);
        var props = look.Schema.GetProperty("properties");
        Assert.Equal("Tiles around you", props.GetProperty("radius").GetProperty("description").GetString());
        Assert.Equal("Short reason, one line", props.GetProperty("reason").GetProperty("description").GetString());
    }

    [Fact]
    public void First_sentence_is_clipped_when_long()
    {
        Assert.Equal("One.", ToolProfile.FirstSentence("One. Two."));
        Assert.Equal("No full stop", ToolProfile.FirstSentence("No full stop"));
        Assert.True(ToolProfile.FirstSentence(new string('a', 400)).Length <= 160);
    }
}
