using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewMCP.Agent.Brain;
using Xunit;

namespace StardewMCP.Tests;

/// <summary>
/// The built-in AI's wire formats, checked without a server: how a conversation with tool calls and results is
/// written for each provider, how their replies are read back, and the texts of the day.
/// </summary>
public class BrainTests
{
    private static readonly HttpClient Http = new();

    private static JsonElement Schema(string json) => JsonDocument.Parse(json.Replace('\'', '"')).RootElement.Clone();

    private static readonly List<LlmTool> Tools = new()
    {
        new LlmTool("walk_to", "Walk to a tile", Schema("{'type':'object','properties':{'x':{'type':'integer'},'y':{'type':'integer'},'reason':{'type':'string'}},'required':['x','y','reason'],'additionalProperties':false}")),
        new LlmTool("status", "Where you are", Schema("{'type':'object','properties':{}}"))
    };

    /// <summary>user → assistant (text + 1 tool call) → tool result + a user note.</summary>
    private static List<ChatMessage> Conversation() => new()
    {
        ChatMessage.User("Good morning"),
        ChatMessage.Assistant("Let me walk.", new[] { new LlmToolCall("call_1", "walk_to", "{\"x\":3,\"y\":4,\"reason\":\"mailbox\"}") }),
        new ChatMessage { Role = "tool", ToolResults = new List<LlmToolResult> { new("call_1", "walk_to", "Arrived at (3,4)", false) }, Text = "[Meeting over]" }
    };

    // ------------------------------------------------------------------ Anthropic

    [Fact]
    public void Anthropic_body_merges_tool_results_and_user_text_into_one_user_turn()
    {
        var p = new AnthropicProvider(Http, "claude-x", "key", null, 1234);
        var body = p.BuildBody("SYS", Conversation(), Tools);

        Assert.Equal("claude-x", body["model"]!.GetValue<string>());
        Assert.Equal(1234, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("SYS", body["system"]!.GetValue<string>());
        Assert.Equal("walk_to", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.NotNull(body["tools"]![0]!["input_schema"]!["properties"]);

        var msgs = (JsonArray)body["messages"]!;
        Assert.Equal(new[] { "user", "assistant", "user" }, msgs.Select(m => m!["role"]!.GetValue<string>()));
        var assistant = (JsonArray)msgs[1]!["content"]!;
        Assert.Equal("text", assistant[0]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", assistant[1]!["type"]!.GetValue<string>());
        Assert.Equal(3, assistant[1]!["input"]!["x"]!.GetValue<int>());
        var last = (JsonArray)msgs[2]!["content"]!;
        Assert.Equal("tool_result", last[0]!["type"]!.GetValue<string>());
        Assert.Equal("call_1", last[0]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("text", last[1]!["type"]!.GetValue<string>());
        Assert.Equal("[Meeting over]", last[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_reply_is_parsed()
    {
        var json = JsonNode.Parse("""
            {"content":[{"type":"text","text":"Off I go."},{"type":"tool_use","id":"toolu_1","name":"walk_to","input":{"x":1,"y":2,"reason":"r"}}],
             "stop_reason":"tool_use","usage":{"input_tokens":120,"output_tokens":30}}
            """)!;
        var r = AnthropicProvider.Parse(json);
        Assert.Equal("Off I go.", r.Text);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal(("toolu_1", "walk_to"), (call.Id, call.Name));
        Assert.Equal(1, JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("x").GetInt32());
        Assert.Equal((120, 30, "tool_use"), (r.InputTokens, r.OutputTokens, r.StopReason));
    }

    // ------------------------------------------------------------------ OpenAI

    [Fact]
    public void OpenAi_body_uses_system_message_tool_role_and_function_tools()
    {
        var p = new OpenAiProvider(Http, "gpt-x", "key", "http://localhost:11434/v1/", 500);
        var body = p.BuildBody("SYS", Conversation(), Tools);

        var msgs = (JsonArray)body["messages"]!;
        Assert.Equal(new[] { "system", "user", "assistant", "tool", "user" }, msgs.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("SYS", msgs[0]!["content"]!.GetValue<string>());
        var call = msgs[2]!["tool_calls"]![0]!;
        Assert.Equal("function", call["type"]!.GetValue<string>());
        Assert.Equal("walk_to", call["function"]!["name"]!.GetValue<string>());
        // arguments stay a JSON *string* in this API
        Assert.True(call["function"]!["arguments"]!.AsValue().TryGetValue<string>(out var argString));
        Assert.Contains("\"x\":3", argString);
        Assert.Equal("call_1", msgs[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("[Meeting over]", msgs[4]!["content"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(500, body["max_completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void OpenAi_reply_is_parsed()
    {
        var json = JsonNode.Parse("""
            {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"call_abc","type":"function","function":{"name":"status","arguments":"{}"}},
                            {"id":"call_def","type":"function","function":{"name":"walk_to","arguments":"not json"}}]}}],
             "usage":{"prompt_tokens":11,"completion_tokens":7}}
            """)!;
        var r = OpenAiProvider.Parse(json);
        Assert.Equal("", r.Text);
        Assert.Equal(2, r.ToolCalls.Count);
        Assert.Equal("call_abc", r.ToolCalls[0].Id);
        Assert.Equal("{}", r.ToolCalls[1].ArgumentsJson); // broken arguments become an empty object, not a crash
        Assert.Equal((11, 7), (r.InputTokens, r.OutputTokens));
    }

    // ------------------------------------------------------------------ Gemini

    [Fact]
    public void Gemini_body_uses_model_role_function_calls_and_filtered_schemas()
    {
        var p = new GeminiProvider(Http, "gemini-x", "key", null, 800);
        var body = p.BuildBody("SYS", Conversation(), Tools);

        Assert.Equal("SYS", body["system_instruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        var contents = (JsonArray)body["contents"]!;
        Assert.Equal(new[] { "user", "model", "user" }, contents.Select(c => c!["role"]!.GetValue<string>()));
        var modelParts = (JsonArray)contents[1]!["parts"]!;
        Assert.Equal("walk_to", modelParts[1]!["functionCall"]!["name"]!.GetValue<string>());
        var response = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("walk_to", response["name"]!.GetValue<string>()); // matched to the call by id
        Assert.Equal("Arrived at (3,4)", response["response"]!["result"]!.GetValue<string>());

        var decl = body["tools"]![0]!["function_declarations"]![0]!;
        Assert.Null(decl["parameters"]!["additionalProperties"]); // Gemini rejects this keyword
        Assert.NotNull(decl["parameters"]!["properties"]!["x"]);
        Assert.Equal(800, body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
    }

    [Fact]
    public void Gemini_reply_is_parsed_and_calls_get_ids()
    {
        var json = JsonNode.Parse("""
            {"candidates":[{"finishReason":"STOP","content":{"role":"model","parts":[
               {"text":"Walking."},{"functionCall":{"name":"walk_to","args":{"x":5,"y":6,"reason":"r"}}}]}}],
             "usageMetadata":{"promptTokenCount":40,"candidatesTokenCount":9}}
            """)!;
        int seq = 0;
        var r = GeminiProvider.Parse(json, ref seq);
        Assert.Equal("Walking.", r.Text);
        var call = Assert.Single(r.ToolCalls);
        Assert.StartsWith("gem_", call.Id);
        Assert.Equal(5, JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("x").GetInt32());
        Assert.Equal((40, 9), (r.InputTokens, r.OutputTokens));
    }

    // ------------------------------------------------------------------ shared pieces

    [Fact]
    public void Schema_filter_keeps_nested_properties_and_items()
    {
        var schema = Schema("{'type':'object','additionalProperties':false,'properties':{'tiles':{'type':'array','items':{'type':'array','items':{'type':'integer'},'additionalProperties':false}},'kind':{'type':'string','enum':['a','b'],'default':'a'}}}");
        var f = SchemaFilter.ForGemini(schema);
        Assert.Null(f["additionalProperties"]);
        Assert.Equal("integer", f["properties"]!["tiles"]!["items"]!["items"]!["type"]!.GetValue<string>());
        Assert.Null(f["properties"]!["tiles"]!["items"]!["additionalProperties"]);
        Assert.Equal(2, ((JsonArray)f["properties"]!["kind"]!["enum"]!).Count);
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(404, true)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    public void Http_errors_are_fatal_only_for_key_and_model_problems(int status, bool fatal)
    {
        var ex = LlmException.FromStatus(status, "{\"error\":{\"message\":\"nope\"}}");
        Assert.Equal(fatal, ex.Fatal);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Provider_factory_knows_defaults_and_missing_keys()
    {
        Assert.Null(LlmProviders.Create(new LlmSettings("none", "", "", ""), Http));
        var local = LlmProviders.Create(new LlmSettings("openai", "", "", "http://localhost:11434/v1"), Http);
        Assert.Equal("gpt-5-mini", local!.Model);
        Assert.Null(LlmProviders.Problem(new LlmSettings("openai", "llama3", "", "http://localhost:11434/v1"))); // local server needs no key
        Assert.Equal("no API key", LlmProviders.Problem(new LlmSettings("anthropic", "", "", "")));
        Assert.Contains("unknown", LlmProviders.Problem(new LlmSettings("bard", "", "k", ""))!);
    }

    [Fact]
    public void System_prompt_fills_every_placeholder()
    {
        var template = "{MODEL_NAME}|{PLAYER_NAME}|{SEAT}|{LANGUAGE}|{TIME_MODE}|{OTHERS}|{SELF}|{PEOPLE}|{LESSONS}|{PLAN}|{NOTES}";
        var text = Prompts.System(template, new Prompts.SystemInputs(
            "m", "Duck", "p1", "Thai", FreezeWhileThinking: false,
            new[] { ("p2", "Gemini", "an AI") }, Self: "", People: "", Lessons: "l1\nl2", Plan: "## Today\nwater", Notes: ""));
        Assert.DoesNotContain("{", text);
        Assert.Contains("keeps running", text);
        Assert.Contains("Gemini (p2, an AI)", text);
        Assert.Contains("(nothing yet", text); // empty self
    }

    [Fact]
    public void Handoff_quotes_todays_plan_and_the_last_results()
    {
        var plan = "## Season\nmoney\n## Today\nwater the parsnips\n## Week\nfish";
        Assert.Equal("water the parsnips", Prompts.PlanSection(plan, "Today"));
        var text = Prompts.Handoff(3, "10:30 AM", plan, "I should hurry", new[] { "walk_to: Arrived" });
        Assert.Contains("day 3", text);
        Assert.Contains("water the parsnips", text);
        Assert.DoesNotContain("fish", text);
        Assert.Contains("I should hurry", text);
        Assert.Contains("walk_to: Arrived", text);
    }

    [Fact]
    public void Tail_keeps_the_last_lines()
    {
        var text = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var tail = Prompts.Tail(text, 10);
        Assert.StartsWith("(older lines left out)", tail);
        Assert.Contains("line 100", tail);
        Assert.DoesNotContain("line 89\n", tail);
    }
}
