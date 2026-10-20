using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StardewMCP.Agent.Brain;

/// <summary>Anthropic Messages API (<c>POST /v1/messages</c>) with tool use.</summary>
public sealed class AnthropicProvider : ILlmProvider
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly int _maxTokens;

    public string Name => "anthropic";
    public string Model { get; }

    public AnthropicProvider(HttpClient http, string model, string apiKey, string? baseUrl, int maxTokens)
    {
        _http = http;
        Model = model;
        _apiKey = apiKey;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl;
        _maxTokens = maxTokens;
    }

    public async Task<LlmResponse> Complete(string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmTool> tools, CancellationToken ct)
    {
        var body = BuildBody(system, messages, tools);
        var req = new HttpRequestMessage(HttpMethod.Post, LlmHttp.Join(_baseUrl, "/v1/messages"));
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        var json = await LlmHttp.PostJson(_http, req, body, ct).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>The request body; public so the tests can check the wire format without a server.</summary>
    public JsonObject BuildBody(string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmTool> tools)
    {
        var toolList = new JsonArray();
        foreach (var t in tools)
            toolList.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.Schema.GetRawText())
            });

        // Anthropic wants user / assistant alternation: a tool-result batch and a user text that follow each other
        // become one user message (tool_result blocks first, then the text).
        var list = new JsonArray();
        JsonArray? openUser = null;
        foreach (var m in messages)
        {
            if (m.Role == "assistant")
            {
                openUser = null;
                var content = new JsonArray();
                if (!string.IsNullOrWhiteSpace(m.Text))
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                foreach (var c in m.ToolCalls)
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = c.Id,
                        ["name"] = c.Name,
                        ["input"] = JsonNode.Parse(LlmHttp.ArgsOrEmpty(c.ArgumentsJson))
                    });
                if (content.Count == 0)
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = "(no text)" });
                list.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                continue;
            }

            if (openUser == null)
            {
                openUser = new JsonArray();
                list.Add(new JsonObject { ["role"] = "user", ["content"] = openUser });
            }
            foreach (var r in m.ToolResults)
                openUser.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = r.CallId,
                    ["content"] = r.Text,
                    ["is_error"] = r.IsError
                });
            if (!string.IsNullOrWhiteSpace(m.Text))
                openUser.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = _maxTokens,
            ["system"] = system,
            ["messages"] = list
        };
        if (toolList.Count > 0)
            body["tools"] = toolList;
        return body;
    }

    public static LlmResponse Parse(JsonNode json)
    {
        var r = new LlmResponse { StopReason = json["stop_reason"]?.GetValue<string>() ?? "" };
        var text = new System.Text.StringBuilder();
        if (json["content"] is JsonArray content)
            foreach (var block in content)
            {
                switch (block?["type"]?.GetValue<string>())
                {
                    case "text":
                        text.Append(block["text"]?.GetValue<string>());
                        break;
                    case "tool_use":
                        r.ToolCalls.Add(new LlmToolCall(
                            block["id"]?.GetValue<string>() ?? $"call_{r.ToolCalls.Count}",
                            block["name"]?.GetValue<string>() ?? "",
                            block["input"]?.ToJsonString() ?? "{}"));
                        break;
                }
            }
        r.Text = text.ToString();
        r.InputTokens = LlmHttp.Int(json["usage"]?["input_tokens"]);
        r.OutputTokens = LlmHttp.Int(json["usage"]?["output_tokens"]);
        return r;
    }
}
