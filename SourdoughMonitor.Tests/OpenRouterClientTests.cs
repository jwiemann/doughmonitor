using System.Net;
using System.Text;
using SourdoughMonitor.Config;
using SourdoughMonitor.Services;

namespace SourdoughMonitor.Tests;

public class OpenRouterClientTests
{
    private const string Ok =
        """{"choices":[{"message":{"content":"{}"}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"cost":0.001}}""";

    private const string Mandatory =
        """{"error":{"message":"Reasoning is mandatory for this endpoint and cannot be disabled.","code":400}}""";

    private sealed class Stub(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            bodies.Add(body);
            return body.Contains("\"enabled\":false")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(Mandatory, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ok, Encoding.UTF8) };
        }
    }

    private static LlmRequest Request() => new("google/gemini-3.8-flash", "s", "u", [1, 2, 3], new { });

    [Fact]
    public async Task ReasoningOff_FallsBackToMinimalForAModelThatRequiresReasoning_AndRemembersIt()
    {
        var bodies = new List<string>();
        var client = new OpenRouterClient(new HttpClient(new Stub(bodies)), new LlmOptions { ApiKey = "k" });

        var first = await client.CompleteAsync(Request(), CancellationToken.None);
        var second = await client.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal(0.001, first.CostUsd);
        Assert.Equal(0.001, second.CostUsd);
        Assert.Equal(3, bodies.Count); // off (rejected), minimal, minimal: no second rejected call
        Assert.Contains("\"enabled\":false", bodies[0]);
        Assert.All(bodies.Skip(1), b => Assert.Contains("\"effort\":\"minimal\"", b));
    }
}
