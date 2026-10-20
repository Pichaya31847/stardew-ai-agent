using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// OpenAI Chat Completions (<c>POST /chat/completions</c>) with function tools. The same wire format is spoken by
/// OpenRouter, Groq, Mistral, Ollama (<c>http://localhost:11434/v1</c>), LM Studio and most other servers, so this is
/// also the provider for local models.
/// </summary>
public sealed class OpenAiProvider : ILlmProvider
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly int _maxTokens;

    public string Name => "openai";
    public string Model { get; }

    public OpenAiProvider(HttpClient http, string model, string apiKey, string? baseUrl, int maxTokens)
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
        var req = new HttpRequestMessage(HttpMethod.Post, LlmHttp.Join(_baseUrl, "/chat/completions"));
        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.Add("Authorization", "Bearer " + _apiKey);
        var json = await LlmHttp.PostJson(_http, req, body, ct).ConfigureAwait(false);
        return Parse(json);
    }

    public JsonObject BuildBody(string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmTool> tools)
    {
        var list = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "assistant":
                    var a = new JsonObject { ["role"] = "assistant", ["content"] = string.IsNullOrEmpty(m.Text) ? null : m.Text };
                    if (m.ToolCalls.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var c in m.ToolCalls)
                            calls.Add(new JsonObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = LlmHttp.ArgsOrEmpty(c.ArgumentsJson) }
                            });
                        a["tool_calls"] = calls;
                    }
                    list.Add(a);
                    break;
                case "tool":
                    foreach (var r in m.ToolResults)
                        list.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.Text });
                    if (!string.IsNullOrWhiteSpace(m.Text))
                        list.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    break;
                default:
                    list.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = list,
            ["max_completion_tokens"] = _maxTokens
        };
        if (tools.Count > 0)
        {
            var toolList = new JsonArray();
            foreach (var t in tools)
                toolList.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.Schema.GetRawText())
                    }
                });
            body["tools"] = toolList;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    public static LlmResponse Parse(JsonNode json)
    {
        var r = new LlmResponse();
        var choice = json["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
        r.StopReason = choice?["finish_reason"]?.GetValue<string>() ?? "";
        var msg = choice?["message"];
        var content = msg?["content"];
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
            r.Text = s;
        else if (content is JsonArray parts)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in parts)
                if (p?["type"]?.GetValue<string>() == "text") sb.Append(p["text"]?.GetValue<string>());
            r.Text = sb.ToString();
        }
        if (msg?["tool_calls"] is JsonArray calls)
            foreach (var c in calls)
            {
                var fn = c?["function"];
                if (fn == null) continue;
                r.ToolCalls.Add(new LlmToolCall(
                    c!["id"]?.GetValue<string>() ?? $"call_{r.ToolCalls.Count}",
                    fn["name"]?.GetValue<string>() ?? "",
                    LlmHttp.ArgsOrEmpty(fn["arguments"]?.GetValue<string>())));
            }
        r.InputTokens = LlmHttp.Int(json["usage"]?["prompt_tokens"]);
        r.OutputTokens = LlmHttp.Int(json["usage"]?["completion_tokens"]);
        return r;
    }
}
