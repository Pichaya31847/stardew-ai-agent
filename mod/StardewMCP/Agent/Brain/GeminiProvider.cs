using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StardewMCP.Agent.Brain;

/// <summary>Google Gemini API (<c>POST /v1beta/models/{model}:generateContent</c>) with function declarations.</summary>
public sealed class GeminiProvider : ILlmProvider
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly int _maxTokens;
    private int _callSeq;

    public string Name => "gemini";
    public string Model { get; }

    public GeminiProvider(HttpClient http, string model, string apiKey, string? baseUrl, int maxTokens)
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
        var req = new HttpRequestMessage(HttpMethod.Post, LlmHttp.Join(_baseUrl, $"/v1beta/models/{Model}:generateContent"));
        req.Headers.Add("x-goog-api-key", _apiKey);
        var json = await LlmHttp.PostJson(_http, req, body, ct).ConfigureAwait(false);
        return Parse(json, ref _callSeq);
    }

    public JsonObject BuildBody(string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmTool> tools)
    {
        var contents = new JsonArray();
        // Gemini matches function responses to calls by name and order, not by id.
        var callNames = new Dictionary<string, string>();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "assistant":
                    var parts = new JsonArray();
                    if (!string.IsNullOrWhiteSpace(m.Text))
                        parts.Add(new JsonObject { ["text"] = m.Text });
                    foreach (var c in m.ToolCalls)
                    {
                        callNames[c.Id] = c.Name;
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject { ["name"] = c.Name, ["args"] = JsonNode.Parse(LlmHttp.ArgsOrEmpty(c.ArgumentsJson)) }
                        });
                    }
                    if (parts.Count == 0)
                        parts.Add(new JsonObject { ["text"] = "(no text)" });
                    contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                    break;
                case "tool":
                    var rp = new JsonArray();
                    foreach (var r in m.ToolResults)
                    {
                        var response = new JsonObject { ["result"] = r.Text };
                        if (r.IsError) response["error"] = true;
                        rp.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = callNames.TryGetValue(r.CallId, out var n) ? n : r.Name,
                                ["response"] = response
                            }
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(m.Text))
                        rp.Add(new JsonObject { ["text"] = m.Text });
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = rp });
                    break;
                default:
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Text ?? "" } } });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } },
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = _maxTokens }
        };
        if (tools.Count > 0)
        {
            var decls = new JsonArray();
            foreach (var t in tools)
                decls.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = SchemaFilter.ForGemini(t.Schema)
                });
            body["tools"] = new JsonArray { new JsonObject { ["function_declarations"] = decls } };
        }
        return body;
    }

    public static LlmResponse Parse(JsonNode json, ref int callSeq)
    {
        var r = new LlmResponse();
        var cand = json["candidates"] is JsonArray { Count: > 0 } cands ? cands[0] : null;
        r.StopReason = cand?["finishReason"]?.GetValue<string>() ?? "";
        var sb = new System.Text.StringBuilder();
        if (cand?["content"]?["parts"] is JsonArray parts)
            foreach (var p in parts)
            {
                if (p == null) continue;
                if (p["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) && p["thought"]?.GetValue<bool>() != true)
                    sb.Append(t);
                if (p["functionCall"] is JsonObject fc)
                    r.ToolCalls.Add(new LlmToolCall(
                        $"gem_{++callSeq}",
                        fc["name"]?.GetValue<string>() ?? "",
                        fc["args"]?.ToJsonString() ?? "{}"));
            }
        r.Text = sb.ToString();
        r.InputTokens = LlmHttp.Int(json["usageMetadata"]?["promptTokenCount"]);
        r.OutputTokens = LlmHttp.Int(json["usageMetadata"]?["candidatesTokenCount"]);
        return r;
    }
}
