using MarvelChatbot;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5080");

// Git-ignored file for the API key, so the committed appsettings.json keeps only a placeholder.
// Environment variables are re-added afterwards so GEMINI_API_KEY still wins.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Fail fast with an actionable message rather than a 500 on the first question.
// The environment variable, when set, overrides the value in the JSON files.
var configuredKey = builder.Configuration["GEMINI_API_KEY"]
    ?? builder.Configuration["Gemini:ApiKey"];

if (string.IsNullOrWhiteSpace(configuredKey) || configuredKey.StartsWith("PASTE-", StringComparison.Ordinal))
{
    Console.Error.WriteLine("""
        ERROR: No Gemini API key configured.

        Create appsettings.Local.json (git-ignored) next to appsettings.json:
            { "Gemini": { "ApiKey": "your-key-here" } }

        Get a key from https://aistudio.google.com/apikey
        (You can also override it with the GEMINI_API_KEY environment variable.)
        """);
    return 1;
}

var documentPath = Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["Gemini:DocumentPath"] ?? "Data/marvel-movies.md");

builder.Services.AddSingleton(new DocumentStore(documentPath));
builder.Services.AddHttpClient<GeminiClient>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(60);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/chat", async (
    ChatRequest request,
    DocumentStore store,
    GeminiClient gemini,
    CancellationToken cancellationToken) =>
{
    var messages = (request.Messages ?? [])
        .Where(message => !string.IsNullOrWhiteSpace(message.Role)
                          && !string.IsNullOrWhiteSpace(message.Text))
        .ToList();

    if (messages.Count == 0 || !messages[^1].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "The last message must be from the user." });
    }

    // Retrieval query: the current question plus the previous one, so a follow-up
    // like "what year was it released?" inherits the earlier subject.
    var userTurns = messages
        .Where(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var query = userTurns.Count > 1
        ? $"{userTurns[^2].Text} {userTurns[^1].Text}"
        : userTurns[^1].Text;

    var context = store.BuildContext(query);

    var systemInstruction = $"""
        You are a helpful assistant that answers questions about Marvel movies.

        Answer ONLY using the reference material below. If the answer is not in it, say you
        do not have that information in your document. Do not use outside knowledge and do
        not guess. Keep answers concise and conversational. Box office figures are
        approximate, so present them as approximate.

        --- REFERENCE MATERIAL ---
        {context}
        --- END REFERENCE MATERIAL ---
        """;

    try
    {
        var reply = await gemini.AskAsync(
            systemInstruction,
            messages.Select(message => new ChatTurn(message.Role, message.Text)),
            cancellationToken);

        return Results.Ok(new { reply });
    }
    catch (Exception exception)
    {
        return Results.Problem(exception.Message, statusCode: 500);
    }
});

var chunkCount = app.Services.GetRequiredService<DocumentStore>().ChunkCount;
Console.WriteLine($"Marvel chatbot ready on http://localhost:5080 ({chunkCount} chunks indexed)");

app.Run();
return 0;

public sealed record ChatMessage(string Role, string Text);

public sealed record ChatRequest(List<ChatMessage>? Messages);
