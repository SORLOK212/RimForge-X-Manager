using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimForge.Steam.Assistant;

/// <summary>Where a neural network answers: any server that speaks the OpenAI chat API.</summary>
/// <param name="BaseUrl">Up to and including the version, e.g. "http://127.0.0.1:11434/v1".</param>
public sealed record LlmEndpoint(string BaseUrl, string Model, string? ApiKey = null);

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A small client for the OpenAI-style chat API, which RimForge's own model (llama.cpp), Ollama, LM Studio,
/// OpenRouter, Groq, Gemini, DeepSeek, Mistral and OpenAI all understand.
/// </summary>
public sealed class LlmClient(HttpClient http)
{
    /// <summary>Asks once and returns the text of the answer. <paramref name="json"/> asks for a JSON object.</summary>
    public async Task<string> CompleteAsync(LlmEndpoint endpoint, string system, string user, bool json, int maxTokens, CancellationToken ct)
    {
        try
        {
            return await SendAsync(endpoint, system, user, json, maxTokens, ct);
        }
        catch (LlmException) when (json)
        {
            // Some servers don't know "response_format": ask again without it, the prompt asks for JSON anyway.
            return await SendAsync(endpoint, system, user, false, maxTokens, ct);
        }
    }

    private async Task<string> SendAsync(LlmEndpoint endpoint, string system, string user, bool json, int maxTokens, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
            ["temperature"] = 0.2,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
        };
        if (json) body["response_format"] = new JsonObject { ["type"] = "json_object" };

        using var request = new HttpRequestMessage(HttpMethod.Post, Url(endpoint.BaseUrl, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(ex.Message, ex);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new LlmException(ErrorText(response.StatusCode, text));
            try
            {
                using var doc = JsonDocument.Parse(text);
                var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
                var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                return StripThinking(content);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                throw new LlmException("unexpected answer", ex);
            }
        }
    }

    /// <summary>The models a server offers (empty if it doesn't say).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(baseUrl, "models"));
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        try
        {
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new LlmException(ErrorText(response.StatusCode, text));
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            return data.EnumerateArray()
                .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                .OfType<string>()
                .Select(id => id.StartsWith("models/") ? id["models/".Length..] : id) // Gemini
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new LlmException("unexpected answer", ex);
        }
    }

    private static Uri Url(string baseUrl, string path) => new(baseUrl.TrimEnd('/') + "/" + path);

    private static string ErrorText(HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.TryGetProperty("error", out var error))
                detail = error.ValueKind == JsonValueKind.String ? error.GetString() ?? ""
                    : error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            detail = body.Length > 200 ? body[..200] : body;
        }
        return $"{(int)status} {status}{(detail.Length > 0 ? ": " + detail : "")}";
    }

    /// <summary>Thinking models put their reasoning in &lt;think&gt; tags: only the answer counts.</summary>
    internal static string StripThinking(string text) =>
        Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();

    /// <summary>The JSON object in an answer, even if the model wrapped it in words or ``` fences.</summary>
    internal static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>Servers that may run on this computer, checked quickly.</summary>
    public static IReadOnlyList<(string Name, string BaseUrl)> LocalServers { get; } =
    [
        ("Ollama", "http://127.0.0.1:11434/v1"),
        ("LM Studio", "http://127.0.0.1:1234/v1"),
    ];
}
