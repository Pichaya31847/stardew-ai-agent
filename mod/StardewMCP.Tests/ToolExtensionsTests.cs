using System;
using System.Collections;
using System.Text.Json;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class ToolExtensionsTests
{
    private static CallContext Run(ToolDef tool, string args)
    {
        var ctx = new CallContext();
        var steps = (IEnumerator)tool.Run(JsonDocument.Parse(args).RootElement, ctx);
        while (steps.MoveNext()) { }
        return ctx;
    }

    [Fact]
    public void An_added_tool_follows_the_contract_and_answers()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"what\":{\"type\":\"string\"},\"reason\":{\"type\":\"string\"}},\"required\":[\"reason\"]}";
        var tool = ToolExtensions.Add("echo_test_tool", "Echo the argument.", schema, json => "got " + JsonDocument.Parse(json).RootElement.GetProperty("what").GetString(), instant: false, core: true);

        Assert.Empty(ToolContract.Check(new[] { tool }));
        Assert.Contains(tool, ToolExtensions.Tools);
        Assert.Contains("echo_test_tool", ToolExtensions.Core);
        var ctx = Run(tool, "{\"what\":\"wood\",\"reason\":\"test\"}");
        Assert.True(ctx.Ok);
        Assert.Equal("got wood", ctx.Result);
    }

    [Fact]
    public void Morning_notes_skip_empty_and_failing_ones()
    {
        ToolExtensions.AddMorningNote(() => "  Your station has no Workbench.  ");
        ToolExtensions.AddMorningNote(() => "");
        ToolExtensions.AddMorningNote(() => throw new InvalidOperationException("broken"));
        var lines = ToolExtensions.MorningNotesText();
        Assert.Contains("Your station has no Workbench.", lines);
        Assert.DoesNotContain("", lines);
        Assert.DoesNotContain(lines, l => l.Contains("broken"));
    }

    [Fact]
    public void A_thrown_message_is_the_failure()
    {
        var tool = ToolExtensions.Add("throw_test_tool", "Always fails.", "{\"type\":\"object\",\"properties\":{}}",
            _ => throw new InvalidOperationException("No Keg in your station."), instant: true, core: false);
        var ctx = Run(tool, "{}");
        Assert.False(ctx.Ok);
        Assert.Equal("No Keg in your station.", ctx.Result);
        Assert.Throws<ArgumentException>(() => ToolExtensions.Add("throw_test_tool", "again", "{}", _ => "", true, false));
    }
}
