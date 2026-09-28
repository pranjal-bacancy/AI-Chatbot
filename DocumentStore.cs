using System.Text;
using System.Text.RegularExpressions;

namespace MarvelChatbot;

/// <summary>A single retrievable piece of the knowledge base.</summary>
/// <param name="Title">Heading path, e.g. "Phase Three (2016-2019) &gt; Avengers: Endgame (2019)".</param>
/// <param name="Body">The prose under that heading (possibly one window of a longer section).</param>
public sealed record Chunk(string Title, string Body)
{
    /// <summary>What actually gets sent to the model: the body always carries its heading.</summary>
    public string Text => $"{Title}\n{Body}";
}

/// <summary>
/// Chunks the Markdown knowledge base at startup and retrieves the most relevant
/// chunks for a question by keyword scoring.
/// </summary>
public sealed class DocumentStore
{
    private const int MaxChunkChars = 900;
    private const int OverlapChars = 150;
    private const int MaxContextChars = 12_000;

    private static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "was", "were", "are", "its", "that", "this", "with", "from",
        "who", "what", "when", "where", "which", "did", "does", "done", "has", "have", "had",
        "you", "your", "about", "tell", "give", "said", "say", "there", "their", "them", "they",
        "then", "than", "into", "out", "can", "could", "would", "should", "will", "shall",
        "been", "being", "also", "any", "all", "how", "why", "not", "movie", "movies", "film",
        "films", "marvel", "please", "know", "much", "many", "made", "make", "one", "two"
    ];

    private readonly List<Chunk> _chunks;

    public DocumentStore(string documentPath)
    {
        if (!File.Exists(documentPath))
        {
            throw new FileNotFoundException(
                $"Knowledge base document not found at '{documentPath}'.", documentPath);
        }

        _chunks = BuildChunks(File.ReadAllText(documentPath));
    }

    public int ChunkCount => _chunks.Count;

    /// <summary>Retrieves the best-matching chunks and joins them into a context block.</summary>
    public string BuildContext(string query, int topN = 4)
    {
        var builder = new StringBuilder();

        foreach (var chunk in Search(query, topN))
        {
            if (builder.Length + chunk.Text.Length > MaxContextChars) break;
            builder.AppendLine(chunk.Text).AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Scores every chunk against the query's keywords: +3 per hit in the heading,
    /// +1 per hit in the body, so a question naming a film outranks a passing mention.
    /// </summary>
    public IReadOnlyList<Chunk> Search(string query, int topN = 4)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0) return Fallback();

        var scored = new List<(Chunk Chunk, int Score)>();

        foreach (var chunk in _chunks)
        {
            var score = 0;
            foreach (var term in terms)
            {
                score += 3 * CountOccurrences(chunk.Title, term);
                score += CountOccurrences(chunk.Body, term);
            }

            if (score > 0) scored.Add((chunk, score));
        }

        if (scored.Count == 0) return Fallback();

        return scored
            .OrderByDescending(entry => entry.Score)
            .Take(topN)
            .Select(entry => entry.Chunk)
            .ToList();
    }

    // ---- Chunking -----------------------------------------------------------

    /// <summary>
    /// Splits on "##" / "###" headings, then splits any oversized section into
    /// overlapping windows. Every window keeps its heading path so it stands alone.
    /// </summary>
    private static List<Chunk> BuildChunks(string markdown)
    {
        var chunks = new List<Chunk>();
        var body = new StringBuilder();
        string h1 = "Document", h2 = "", h3 = "";

        void Flush()
        {
            var text = body.ToString().Trim();
            body.Clear();
            if (text.Length == 0) return;

            var title = BuildTitle(h1, h2, h3);
            foreach (var window in SplitWithOverlap(text))
            {
                chunks.Add(new Chunk(title, window));
            }
        }

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                h3 = trimmed[4..].Trim();
            }
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                h2 = trimmed[3..].Trim();
                h3 = "";
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush();
                h1 = trimmed[2..].Trim();
                h2 = "";
                h3 = "";
            }
            else
            {
                body.AppendLine(trimmed);
            }
        }

        Flush();
        return chunks;
    }

    private static string BuildTitle(string h1, string h2, string h3)
    {
        if (h2.Length == 0) return h1;
        return h3.Length == 0 ? h2 : $"{h2} > {h3}";
    }

    /// <summary>
    /// Yields the text as-is when it fits, otherwise as successive windows of
    /// <see cref="MaxChunkChars"/> that overlap by <see cref="OverlapChars"/>, cutting
    /// on whitespace so a window never begins or ends mid-word.
    /// </summary>
    private static IEnumerable<string> SplitWithOverlap(string text)
    {
        if (text.Length <= MaxChunkChars)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            if (text.Length - start <= MaxChunkChars)
            {
                yield return text[start..].Trim();
                yield break;
            }

            var end = start + MaxChunkChars;

            // Walk back to the nearest whitespace so we cut between words.
            var cut = end;
            while (cut > start && !char.IsWhiteSpace(text[cut])) cut--;
            if (cut <= start) cut = end; // A single word longer than the window: hard cut.

            yield return text[start..cut].Trim();

            // Step back by the overlap, but always make forward progress.
            var next = cut - OverlapChars;
            if (next > start)
            {
                // Align the overlap forward to a word boundary too, so the next
                // window does not begin mid-word.
                while (next < cut && !char.IsWhiteSpace(text[next])) next++;
                start = next < cut ? next : cut;
            }
            else
            {
                start = cut;
            }
        }
    }

    // ---- Retrieval helpers --------------------------------------------------

    private static List<string> Tokenize(string query)
    {
        return Regex.Split(query.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(token => token.Length >= 3 && !StopWords.Contains(token))
            .Distinct()
            .ToList();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Keeps the model grounded even when a question shares no keywords with the document.</summary>
    private IReadOnlyList<Chunk> Fallback()
    {
        var overview = _chunks
            .Where(chunk => chunk.Title.Contains("Overview", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return overview.Count > 0 ? overview : _chunks.Take(2).ToList();
    }
}
