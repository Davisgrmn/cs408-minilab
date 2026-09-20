using System.Net;
using System.Text;
using AssignmentTracker.Services;
using Microsoft.Extensions.Configuration;

var checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
CanvasClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply, string? token = "test-token") =>
    new(new HttpClient(new FakeHandler(reply)), new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["CANVAS_API_TOKEN"] = token }).Build());
HttpResponseMessage Json(string text, string? next = null)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    if (next is not null) response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
    return response;
}
async Task Error(CanvasClient client, string expected)
{
    try { await client.ListAsync<Course>("/api/v1/courses"); throw new Exception("Expected an error"); }
    catch (CanvasException error) { Check(error.Message.Contains(expected), error.Message); }
}

var calls = 0;
var paginated = Client(request =>
{
    calls++;
    Check(request.Headers.Authorization?.ToString() == "Bearer test-token", "Bearer token missing");
    Check(request.Headers.UserAgent.ToString() == "AssignmentTracker/1.0", "Canvas requires a User-Agent header");
    return request.RequestUri!.Query.Contains("page=2") ? Json("[{\"id\":2}]") :
        Json("[{\"id\":1}]", $"https://boisestatecanvas.instructure.com{request.RequestUri.AbsolutePath}?page=2");
});
Check((await paginated.ListAsync<Course>("/api/v1/courses")).Count == 2, "Course pagination");
Check((await paginated.ListAsync<CanvasAssignment>("/api/v1/courses/1/assignments")).Count == 2, "Assignment pagination");
Check(calls == 4, "Wrong request count");
await Error(Client(_ => Json("[]"), null), "Missing CANVAS_API_TOKEN");
foreach (var status in new[] { 401, 403, 429, 500 })
    await Error(Client(_ => new HttpResponseMessage((HttpStatusCode)status)), status switch { 401 => "rejected", 403 => "denied", 429 => "too many", _ => "HTTP 500" });
await Error(Client(_ => throw new HttpRequestException()), "Could not reach");
await Error(Client(_ => throw new TaskCanceledException()), "too long");
await Error(Client(_ => Json("not json")), "unreadable");
await Error(Client(_ => Json("[]", "https://foreign.example/page")), "invalid pagination");
await Error(Client(_ => Json("[]", "https://boisestatecanvas.instructure.com/api/v1/courses")), "invalid pagination");

var tracker = Client(request => request.RequestUri!.AbsolutePath switch
{
    "/api/v1/courses" => Json("""[{"id":1,"name":"CS 408"},{"id":2,"name":"Restricted"}]"""),
    "/api/v1/courses/2/assignments" => new(HttpStatusCode.Forbidden),
    _ => Json("""[{"name":"Later","due_at":"2026-09-22T12:00:00Z"},{"name":"Urgent","due_at":"2026-09-17T18:00:00Z"},{"name":"Soon","due_at":"2026-09-19T12:00:00Z"},{"name":"Past","due_at":"2026-09-01T00:00:00Z"},{"name":"Undated","due_at":null},{"name":"Outside","due_at":"2026-10-22T00:00:00Z"}]""")
});
var result = await tracker.GetUpcomingAsync(7, DateTimeOffset.Parse("2026-09-17T12:00:00Z"));
Check(result.Assignments.Select(row => row.Urgency).SequenceEqual(new[] { "urgent", "soon", "later" }), "Filtering or sorting failed");
Check(result.Warnings.Count == 1 && result.Warnings[0].Contains("Restricted"), "Partial failure missing");
Console.WriteLine($"Passed {checks} checks.");

class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
}
