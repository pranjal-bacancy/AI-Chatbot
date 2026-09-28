# Marvel Movies Chatbot

A document-grounded chatbot: it answers questions about Marvel movies using only
`Data/marvel-movies.md`, via the Gemini API. ASP.NET Core Minimal API + a static
HTML page. No NuGet packages.

## Run it

Create `appsettings.Local.json` next to `appsettings.json` with your key (get one at
<https://aistudio.google.com/apikey>). This file is git-ignored:

```json
{
  "Gemini": {
    "ApiKey": "your-key-here"
  }
}
```

Then:

```powershell
dotnet run
```

and open <http://localhost:5080>.

The key stays server-side and is never sent to the browser. The app refuses to start
while the placeholder value is still in place, so a forgotten paste gives you a clear
message instead of a 400 from Google.

**Note:** never put the real key in `appsettings.json` — that file is committed. Use
`appsettings.Local.json` or the `GEMINI_API_KEY` environment variable, which overrides
both JSON files when set.

## How it works

```
User -> Chat Interface (wwwroot/index.html)
     -> Conversation History (browser-side array, replayed each turn)
     -> Retriever (DocumentStore.Search)
     -> Relevant Context (top 4 chunks)
     -> Gemini (GeminiClient -> generateContent)
     -> Response
```

The server is stateless: the browser owns the conversation history and posts the whole
array to `POST /chat` on every turn.

**Chunking** (`DocumentStore.cs`): the Markdown is split on `##` / `###` headings, then
any section over 900 characters is cut into windows overlapping by 150 characters, on
word boundaries. Every window is prefixed with its heading path, so a window pulled out
of the middle of a section still identifies what it is about. The current document
produces 66 chunks.

**Retrieval**: question keywords are scored against each chunk — +3 per hit in the
heading, +1 per hit in the body — and the top 4 chunks become the context. For
follow-up questions the retrieval query is the current question plus the previous one,
so "what year was it released?" still has something to match on.

**Grounding**: the system instruction tells the model to answer only from the supplied
context and to say so when the answer is not there.

## Configuration

`appsettings.json`:

| Key | Default | Notes |
|---|---|---|
| `Gemini:ApiKey` | placeholder | Your AI Studio key. Overridden by the `GEMINI_API_KEY` env var. |
| `Gemini:Model` | `gemini-3.5-flash-lite` | Change if your key cannot reach this model. |
| `Gemini:DocumentPath` | `Data/marvel-movies.md` | Swap in any Markdown file with `##` headings. |

To see which models your key can use:

```powershell
Invoke-RestMethod -Uri "https://generativelanguage.googleapis.com/v1beta/models" `
  -Headers @{ "x-goog-api-key" = $env:GEMINI_API_KEY } |
  Select-Object -ExpandProperty models | Select-Object name
```

## Troubleshooting

| Symptom | Cause |
|---|---|
| Refuses to start, "No Gemini API key configured" | Placeholder still in `appsettings.json`. |
| `API key not valid` (400) | Wrong or unactivated key. |
| 404 on the model path | That model ID is not available to your key; change `Gemini:Model`. |
| "I couldn't produce an answer" | Safety filter returned no candidate. |
| Bot says it lacks the information | The answer genuinely is not in `Data/marvel-movies.md`. |
