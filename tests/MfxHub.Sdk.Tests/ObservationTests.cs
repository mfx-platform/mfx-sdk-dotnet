using System.Net;
using System.Text;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// What the client tells its caller about the platform's answers: the key's budget, and the timing
/// of every attempt, without either ever failing a call.
/// </summary>
public sealed class ObservationTests
{
    private sealed class Answering(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answers[_next++]());
    }

    private static HttpResponseMessage With(HttpStatusCode status, string? remaining, string? source)
    {
        var answer = new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (remaining is not null)
        {
            answer.Headers.TryAddWithoutValidation("X-MFX-Budget-Remaining", remaining);
        }
        if (source is not null)
        {
            answer.Headers.TryAddWithoutValidation("X-MFX-Budget-Source", source);
        }
        return answer;
    }

    /// <summary>
    /// Given answers carrying the key's budget, one whose budget is not a number, and one with none /
    /// When the client reads them / Then the budget is null before the first answer, the platform's
    /// reading after it, and the last good reading when a later answer says nothing usable; each
    /// attempt is told to the listener with its status, a listener that throws fails nothing, and a
    /// retried read is two attempts.
    /// </summary>
    [Fact]
    public async Task The_budget_and_each_attempt_are_reported_without_failing_a_call()
    {
        var told = new List<CallTiming>();
        using var handler = new Answering(
            () => With(HttpStatusCode.OK, "4980", "counted"),
            () => With(HttpStatusCode.OK, "plenty", "counted"),
            () => With(HttpStatusCode.TooManyRequests, null, null),
            () => With(HttpStatusCode.OK, null, null));
        using var client = new MfxClient(new Uri("https://api.example.test/v1"), "mfxk_1", SigningKey.FromSeed(SigningTests.TestSeed),
            new MfxClientOptions
            {
                Handler = handler,
                Pause = (_, _) => Task.CompletedTask,
                OnCall = timing =>
                {
                    told.Add(timing);
                    throw new InvalidOperationException("a listener's bug");
                },
            });

        Assert.Null(client.Budget);
        await client.ReadAsync(new MfxRequest(HttpMethod.Get, "/terminals"), CancellationToken.None);
        Assert.Equal(new Budget(4980, "counted"), client.Budget);
        await client.ReadAsync(new MfxRequest(HttpMethod.Get, "/farms"), CancellationToken.None);
        await client.ReadAsync(new MfxRequest(HttpMethod.Get, "/usage"), CancellationToken.None);

        Assert.Equal(new Budget(4980, "counted"), client.Budget);
        Assert.Equal(
            [("GET /terminals", (int?)200), ("GET /farms", 200), ("GET /usage", 429), ("GET /usage", 200)],
            told.Select(t => (t.What, t.Status)));
        Assert.All(told, t => Assert.True(t.Elapsed >= TimeSpan.Zero));
    }
}
