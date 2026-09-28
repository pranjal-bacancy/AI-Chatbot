using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarvelChatbot;

/// <summary>One turn of the conversation. Role is "user" or "model" — the names Gemini itself uses.</summary>
public sealed record ChatTurn(string Role, string Text);

/// <summary>
/// Calls the Gemini generateContent REST endpoint. Stateless: the full conversation
/// history is passed in on every call.
/// </summary>
public sealed class GeminiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        // appsettings.json is the primary source; the environment variable overrides it.
        _apiKey = configuration["GEMINI_API_KEY"]
            ?? configuration["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("No Gemini API key configured.");
        _model = configuration["Gemini:Model"] ?? "gemini-3.5-flash-lite";
    }

    public async Task<string> AskAsync(
        string systemInstruction,
        IEnumerable<ChatTurn> history,
        CancellationToken cancellationToken)
    {
        var payload = new GeminiRequest(
            SystemInstruction: new GeminiContent(null, [new Part(systemInstruction)]),
            Contents: history.Select(turn => new GeminiContent(turn.Role, [new Part(turn.Text)])).ToArray(),
            GenerationConfig: new GenerationConfig(Temperature: 0.3, MaxOutputTokens: 800));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"v1beta/models/{_model}:generateContent")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add("x-goog-api-key", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Surface Google's own error text — a bad key or wrong model ID says so plainly.
            throw new HttpRequestException(
                $"Gemini returned {(int)response.StatusCode} ({response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<GeminiResponse>(body, JsonOptions);
        var parts = parsed?.Candidates?.FirstOrDefault()?.Content?.Parts;
        var text = parts is null ? null : string.Concat(parts.Select(part => part.Text));

        // No candidate usually means the response was blocked by a safety filter.
        return string.IsNullOrWhiteSpace(text)
            ? "I couldn't produce an answer for that. Try rephrasing your question."
            : text.Trim();
    }
}

// ---- Request / response shapes, matching the documented REST schema ----------

internal sealed record GeminiRequest(
    GeminiContent SystemInstruction,
    GeminiContent[] Contents,
    GenerationConfig GenerationConfig);

internal sealed record GeminiContent(string? Role, Part[]? Parts);

internal sealed record Part(string? Text);

internal sealed record GenerationConfig(double Temperature, int MaxOutputTokens);

internal sealed record GeminiResponse(Candidate[]? Candidates);

internal sealed record Candidate(GeminiContent? Content);
