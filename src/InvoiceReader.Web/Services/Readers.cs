using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceReader.Web.Services;

public interface IInvoiceReader
{
    /// <summary>Name shown in the UI (Settings page).</summary>
    string Name { get; }

    /// <summary>True if a stronger model can be used for a second read.</summary>
    bool HasStrongModel { get; }

    Task<ReadResult> ReadAsync(byte[] file, string contentType, bool useStrongModel, CancellationToken ct);
}

public class AiOptions
{
    /// <summary>"Demo", "Claude" or "Gemini".</summary>
    public string Provider { get; set; } = "Demo";
    public string? ClaudeApiKey { get; set; }
    public string ClaudeModel { get; set; } = "claude-haiku-5-5";
    public string ClaudeStrongModel { get; set; } = "claude-sonnet-5-5";

    /// <summary>
    /// How much the model thinks before answering: "low", "medium" or "high". Copying fields off an
    /// invoice needs little; higher costs more tokens and time.
    /// </summary>
    public string ClaudeEffort { get; set; } = "low";
    public string? GeminiApiKey { get; set; }
    public string GeminiModel { get; set; } = "gemini-3.1-flash-lite";
}

/// <summary>Reads invoices with the Claude API (Messages API, image or PDF input).</summary>
public class ClaudeInvoiceReader : IInvoiceReader
{
    private readonly HttpClient _http;
    private readonly AiOptions _opt;

    public ClaudeInvoiceReader(HttpClient http, AiOptions opt)
    {
        _http = http;
        _opt = opt;
    }

    /// <summary>Thinking plus the JSON answer; at effort "low" a read uses far less.</summary>
    public const int MaxTokens = 16000;

    public string Name => $"Claude ({_opt.ClaudeModel}, provjera: {_opt.ClaudeStrongModel})";
    public bool HasStrongModel => !string.IsNullOrWhiteSpace(_opt.ClaudeStrongModel);

    public async Task<ReadResult> ReadAsync(byte[] file, string contentType, bool useStrongModel, CancellationToken ct)
    {
        var model = useStrongModel ? _opt.ClaudeStrongModel : _opt.ClaudeModel;
        var base64 = Convert.ToBase64String(file);

        JsonObject fileBlock = contentType == "application/pdf"
            ? new JsonObject
            {
                ["type"] = "document",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = base64 }
            }
            : new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = contentType, ["data"] = base64 }
            };

        // Current Claude models think before they answer, and thinking counts towards max_tokens:
        // leave plenty of room so the JSON answer is never cut off. Effort keeps the thinking short.
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokens,
            ["output_config"] = new JsonObject { ["effort"] = _opt.ClaudeEffort },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { fileBlock, new JsonObject { ["type"] = "text", ["text"] = AiJson.Instruction } }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", _opt.ClaudeApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Claude API {(int)res.StatusCode}: {Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        if (stopReason == "max_tokens")
            throw new InvalidOperationException($"Claude ({model}) je prekinuo odgovor prije kraja (max_tokens). Pokušajte ponovo.");
        if (stopReason == "refusal")
            throw new InvalidOperationException($"Claude ({model}) je odbio pročitati ovaj dokument.");

        var answer = string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString()));

        return new ReadResult(AiJson.Parse(answer), AiJson.ExtractObject(answer), model);
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// Reads invoices with the Gemini API. The free tier is fine for the demo with SAMPLE invoices only:
/// Google may use free-tier data to improve its products, so never send real client invoices there.
/// </summary>
public class GeminiInvoiceReader : IInvoiceReader
{
    private readonly HttpClient _http;
    private readonly AiOptions _opt;

    public GeminiInvoiceReader(HttpClient http, AiOptions opt)
    {
        _http = http;
        _opt = opt;
    }

    public string Name => $"Gemini ({_opt.GeminiModel}) — samo za demo podatke";
    public bool HasStrongModel => false;

    public async Task<ReadResult> ReadAsync(byte[] file, string contentType, bool useStrongModel, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject { ["mime_type"] = contentType, ["data"] = Convert.ToBase64String(file) }
                        },
                        new JsonObject { ["text"] = AiJson.Instruction }
                    }
                }
            },
            ["generationConfig"] = new JsonObject { ["response_mime_type"] = "application/json" }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_opt.GeminiModel}:generateContent";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-goog-api-key", _opt.GeminiApiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini API {(int)res.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");

        using var doc = JsonDocument.Parse(text);
        var answer = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0]
            .GetProperty("text").GetString() ?? "";

        return new ReadResult(AiJson.Parse(answer), AiJson.ExtractObject(answer), _opt.GeminiModel);
    }
}

/// <summary>
/// Works with no API key: recognises the bundled sample invoices by their SHA-256 hash and returns
/// their known data after a short pause, so the demo looks like real reading.
/// Any other file comes back empty, for manual entry.
/// </summary>
public class DemoInvoiceReader : IInvoiceReader
{
    private readonly Dictionary<string, JsonElement> _samples;

    public DemoInvoiceReader(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "SampleInvoices", "samples.json");
        _samples = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var s in doc.RootElement.EnumerateArray())
            _samples[s.GetProperty("sha256").GetString()!] = s.GetProperty("answer").Clone();
    }

    public string Name => "Demo način (bez AI ključa, prepoznaje samo priložene primjere)";
    public bool HasStrongModel => false;

    public async Task<ReadResult> ReadAsync(byte[] file, string contentType, bool useStrongModel, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);

        var hash = Convert.ToHexString(SHA256.HashData(file));
        if (_samples.TryGetValue(hash, out var answer))
        {
            var json = answer.GetRawText();
            return new ReadResult(AiJson.Parse(json), json, "demo");
        }

        var empty = new AiInvoice();
        return new ReadResult(empty, "{}", "demo",
            "Demo način: ovo nije priloženi primjer, pa podaci nisu očitani. Unesite ih ručno ili uključite AI ključ.");
    }
}
