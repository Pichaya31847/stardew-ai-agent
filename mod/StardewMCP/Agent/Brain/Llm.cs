using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StardewMCP.Agent.Brain;

// The viewer edition's own AI loop (no external MCP client, no runner): these types describe one chat turn with a
// tool-calling model in a provider-neutral way. The three providers (Anthropic, OpenAI-compatible, Gemini) translate
// them to their wire formats. Nothing here touches the game, so the unit tests compile this file.

/// <summary>A tool offered to the model: the MCP tool's name, description and JSON schema.</summary>
public sealed record LlmTool(string Name, string Description, JsonElement Schema);

/// <summary>
/// A tool the model asked for. <see cref="Id"/> is the provider's call id (Gemini has none, so one is made up).
/// <see cref="Signature"/> is Gemini's opaque <c>thoughtSignature</c>, which must go back unchanged with the call.
/// </summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson, string? Signature = null);

/// <summary>What a tool answered, going back to the model.</summary>
public sealed record LlmToolResult(string CallId, string Name, string Text, bool IsError);

/// <summary>One message of the conversation. Role is "user", "assistant" or "tool" (a batch of tool results).</summary>
public sealed class ChatMessage
{
    public string Role { get; init; } = "user";
    public string? Text { get; init; }
    public List<LlmToolCall> ToolCalls { get; init; } = new();
    public List<LlmToolResult> ToolResults { get; init; } = new();

    public static ChatMessage User(string text) => new() { Role = "user", Text = text };
    public static ChatMessage Assistant(string? text, IEnumerable<LlmToolCall> calls) => new() { Role = "assistant", Text = text, ToolCalls = new List<LlmToolCall>(calls) };
    public static ChatMessage Tool(IEnumerable<LlmToolResult> results) => new() { Role = "tool", ToolResults = new List<LlmToolResult>(results) };

    /// <summary>Rough size for context budgeting (characters of text, arguments and results).</summary>
    public int Chars
    {
        get
        {
            int n = Text?.Length ?? 0;
            foreach (var c in ToolCalls) n += c.Name.Length + c.ArgumentsJson.Length;
            foreach (var r in ToolResults) n += r.Text.Length;
            return n;
        }
    }
}

/// <summary>The model's answer to one request.</summary>
public sealed class LlmResponse
{
    public string Text { get; set; } = "";
    public List<LlmToolCall> ToolCalls { get; } = new();
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public string StopReason { get; set; } = "";
}

/// <summary>A failed request. <see cref="Fatal"/> errors (bad key, unknown model) are not retried.</summary>
public sealed class LlmException : Exception
{
    public int Status { get; }
    public bool Fatal { get; }

    public LlmException(int status, string message, bool fatal = false) : base(message)
    {
        Status = status;
        Fatal = fatal;
    }

    /// <summary>401/403 (key), 400/404 (model or request) are the caller's problem; 408/429/5xx are worth a retry.</summary>
    public static LlmException FromStatus(int status, string body)
    {
        string detail = ExtractMessage(body);
        bool fatal = status is 400 or 401 or 403 or 404 or 422;
        string hint = status switch
        {
            401 or 403 => "check the API key",
            404 => "check the model name and base URL",
            429 => "rate limit or no credit",
            _ => ""
        };
        return new LlmException(status, $"HTTP {status}{(hint.Length > 0 ? $" ({hint})" : "")}: {detail}", fatal);
    }

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty response)";
        try
        {
            var node = JsonNode.Parse(body);
            var err = node?["error"];
            string? msg = err is JsonObject ? err["message"]?.GetValue<string>() : err?.ToString();
            if (!string.IsNullOrWhiteSpace(msg)) return Trim(msg!);
        }
        catch (JsonException) { }
        return Trim(body);
    }

    private static string Trim(string s) => s.Length > 300 ? s.Substring(0, 300) + "…" : s;
}

/// <summary>One chat-completion backend.</summary>
public interface ILlmProvider
{
    /// <summary>"anthropic", "openai" or "gemini".</summary>
    string Name { get; }
    string Model { get; }

    Task<LlmResponse> Complete(string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmTool> tools, CancellationToken ct);
}

/// <summary>What a provider needs from the seat's configuration, without depending on ModConfig (tests build it directly).</summary>
public sealed record LlmSettings(string Provider, string Model, string ApiKey, string BaseUrl, int MaxOutputTokens = 4096);

public static class LlmProviders
{
    public static readonly string[] Names = { "none", "anthropic", "openai", "gemini" };

    /// <summary>The model a provider plays with when none is configured.</summary>
    public static string DefaultModel(string provider) => provider switch
    {
        "anthropic" => "claude-haiku-5-5",
        "openai" => "gpt-5-mini",
        "gemini" => "gemini-3.8-flash",
        _ => ""
    };

    /// <summary>Build the provider for a seat, or null when the seat has no built-in brain ("none" = an external MCP client drives it).</summary>
    public static ILlmProvider? Create(LlmSettings s, HttpClient http)
    {
        string provider = (s.Provider ?? "").Trim().ToLowerInvariant();
        string model = string.IsNullOrWhiteSpace(s.Model) ? DefaultModel(provider) : s.Model.Trim();
        return provider switch
        {
            "anthropic" => new AnthropicProvider(http, model, s.ApiKey, s.BaseUrl, s.MaxOutputTokens),
            "openai" => new OpenAiProvider(http, model, s.ApiKey, s.BaseUrl, s.MaxOutputTokens),
            "gemini" => new GeminiProvider(http, model, s.ApiKey, s.BaseUrl, s.MaxOutputTokens),
            _ => null
        };
    }

    /// <summary>Why a seat cannot start, or null when its settings look complete.</summary>
    public static string? Problem(LlmSettings s)
    {
        string provider = (s.Provider ?? "").Trim().ToLowerInvariant();
        if (provider is "" or "none") return null;
        if (Array.IndexOf(Names, provider) < 0) return $"unknown AI provider \"{s.Provider}\" (use anthropic, openai or gemini)";
        bool local = provider == "openai" && !string.IsNullOrWhiteSpace(s.BaseUrl) &&
                     (s.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) || s.BaseUrl.Contains("127.0.0.1"));
        if (string.IsNullOrWhiteSpace(s.ApiKey) && !local) return "no API key";
        return null;
    }
}

/// <summary>Shared HTTP plumbing of the three providers.</summary>
internal static class LlmHttp
{
    public static async Task<JsonNode> PostJson(HttpClient http, HttpRequestMessage req, JsonNode body, CancellationToken ct)
    {
        req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(0, $"connection failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(408, "request timed out");
        }
        string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw LlmException.FromStatus((int)res.StatusCode, text);
        try
        {
            return JsonNode.Parse(text) ?? throw new LlmException(0, "empty JSON reply");
        }
        catch (JsonException ex)
        {
            throw new LlmException(0, $"unreadable reply: {ex.Message}");
        }
    }

    public static string Join(string baseUrl, string path)
    {
        baseUrl = baseUrl.TrimEnd('/');
        return baseUrl + (path.StartsWith("/") ? path : "/" + path);
    }

    /// <summary>Parse tool-call arguments the model produced; an unreadable string becomes an empty object.</summary>
    public static string ArgsOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? json : "{}";
        }
        catch (JsonException) { return "{}"; }
    }

    public static int Int(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Min(l, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return 0;
    }

    /// <summary>JsonNode.DeepClone arrived after .NET 6: clone through text.</summary>
    public static JsonNode Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;
}

/// <summary>JSON-schema clean-up for providers that accept only a subset of the keywords (Gemini).</summary>
public static class SchemaFilter
{
    private static readonly HashSet<string> GeminiKeys = new(StringComparer.Ordinal)
    {
        "type", "format", "title", "description", "nullable", "enum", "maxItems", "minItems", "properties", "required",
        "minProperties", "maxProperties", "minLength", "maxLength", "pattern", "example", "anyOf", "propertyOrdering",
        "default", "items", "minimum", "maximum"
    };

    /// <summary>Drop every keyword Gemini's Schema does not know (additionalProperties, $schema, examples...).</summary>
    public static JsonNode ForGemini(JsonElement schema) => Filter(JsonNode.Parse(schema.GetRawText())!, GeminiKeys);

    private static JsonNode Filter(JsonNode node, HashSet<string> keep)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (value == null) continue;
                    if (key == "properties" && value is JsonObject props)
                    {
                        var p = new JsonObject();
                        foreach (var (name, sub) in props)
                            if (sub != null) p[name] = Filter(sub, keep);
                        result[key] = p;
                    }
                    else if (keep.Contains(key))
                        result[key] = key is "items" or "anyOf" ? Filter(value, keep) : LlmHttp.Clone(value);
                }
                return result;
            case JsonArray arr:
                var a = new JsonArray();
                foreach (var item in arr)
                    if (item != null) a.Add(Filter(item, keep));
                return a;
            default:
                return LlmHttp.Clone(node);
        }
    }
}
