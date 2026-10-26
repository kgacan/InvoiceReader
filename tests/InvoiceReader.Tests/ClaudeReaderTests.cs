using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InvoiceReader.Web.Services;

namespace InvoiceReader.Tests;

/// <summary>The Claude request and answer handling, against a fake API (no key, no cost).</summary>
public class ClaudeReaderTests
{
    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly string _response;
        public JsonNode? Request { get; private set; }

        public FakeApi(string response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (ClaudeInvoiceReader Reader, FakeApi Api) Create(string response)
    {
        var api = new FakeApi(response);
        var options = new AiOptions { Provider = "Claude", ClaudeApiKey = "test-key" };
        return (new ClaudeInvoiceReader(new HttpClient(api), options), api);
    }

    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xD9 };

    [Fact]
    public async Task Request_leaves_room_for_thinking_and_asks_for_low_effort()
    {
        var (reader, api) = Create("""
            {"stop_reason":"end_turn","content":[
              {"type":"thinking","thinking":""},
              {"type":"text","text":"{\"invoice_number\":\"TD-1\",\"total_amount\":117.00}"}]}
            """);

        var result = await reader.ReadAsync(Jpeg, "image/jpeg", useStrongModel: false, CancellationToken.None);

        Assert.Equal("claude-haiku-5-5", (string?)api.Request!["model"]);
        Assert.Equal(ClaudeInvoiceReader.MaxTokens, (int?)api.Request["max_tokens"]);
        Assert.Equal("low", (string?)api.Request["output_config"]!["effort"]);
        Assert.Equal("TD-1", result.Invoice.InvoiceNumber);
        Assert.Equal(117.00m, result.Invoice.TotalAmount);
    }

    [Fact]
    public async Task Second_opinion_uses_the_strong_model()
    {
        var (reader, api) = Create("""{"stop_reason":"end_turn","content":[{"type":"text","text":"{}"}]}""");

        await reader.ReadAsync(Jpeg, "image/jpeg", useStrongModel: true, CancellationToken.None);

        Assert.Equal("claude-sonnet-5-5", (string?)api.Request!["model"]);
    }

    [Theory]
    [InlineData("max_tokens", "prekinuo")]
    [InlineData("refusal", "odbio")]
    public async Task Cut_off_or_refused_answer_is_a_clear_error(string stopReason, string expected)
    {
        var (reader, _) = Create($$"""{"stop_reason":"{{stopReason}}","content":[{"type":"text","text":"{\"invoice_num"}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Jpeg, "image/jpeg", useStrongModel: false, CancellationToken.None));
        Assert.Contains(expected, ex.Message);
    }
}
