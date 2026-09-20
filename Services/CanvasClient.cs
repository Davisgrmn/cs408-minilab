using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AssignmentTracker.Services;

public class CanvasException(string message) : Exception(message);
public record Course(long Id, string? Name);
public record CanvasAssignment(string? Name, [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt);
public record AssignmentRow(string Name, string Course, DateTimeOffset Due, string Urgency, string Label);
public record TrackerResult(List<AssignmentRow> Assignments, List<string> Warnings);

public class CanvasClient(HttpClient http, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<List<T>> ListAsync<T>(string path)
    {
        var token = configuration["CANVAS_API_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
            throw new CanvasException("Missing CANVAS_API_TOKEN. Add it to .env and restart the app.");
        var baseUrl = configuration["CANVAS_BASE_URL"] ?? "https://boisestatecanvas.instructure.com";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.UserInfo.Length > 0)
            throw new CanvasException("CANVAS_BASE_URL must be a valid HTTPS Canvas address.");
        Uri? next = new(origin, path);
        var items = new List<T>();
        var visited = new HashSet<string>();
        while (next is not null)
        {
            // Pagination must never forward the token outside the configured Canvas origin.
            if (next.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) ||
                next.UserInfo.Length > 0 || !visited.Add(next.AbsoluteUri))
                throw new CanvasException("Canvas returned an invalid pagination link.");
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Canvas rejects API requests that do not identify their client.
            request.Headers.UserAgent.ParseAdd("AssignmentTracker/1.0");
            try
            {
                using var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    throw new CanvasException((int)response.StatusCode switch
                    {
                        401 => "Canvas rejected the API token. Check that it is correct and has not expired.",
                        403 => "Canvas denied access. Check your token permissions and course access.",
                        429 => "Canvas is receiving too many requests. Wait a moment and try again.",
                        _ => $"Canvas returned HTTP {(int)response.StatusCode}. Please try again later."
                    });
                var page = await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions)
                    ?? throw new CanvasException("Canvas returned an empty response.");
                items.AddRange(page);
                next = null;
                if (response.Headers.TryGetValues("Link", out var links))
                {
                    foreach (var part in string.Join(",", links).Split(','))
                    {
                        if (!Regex.IsMatch(part, "rel=\"next\"")) continue;
                        var match = Regex.Match(part, "<([^>]+)>");
                        if (!match.Success || !Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out next))
                            throw new CanvasException("Canvas returned an invalid pagination link.");
                        break;
                    }
                }
            }
            catch (HttpRequestException) { throw new CanvasException("Could not reach Canvas. Check your connection and Canvas address, then try again."); }
            catch (OperationCanceledException) { throw new CanvasException("Canvas took too long to respond. Please try again."); }
            catch (JsonException) { throw new CanvasException("Canvas returned an unreadable response. Please try again."); }
        }
        return items;
    }

    public async Task<TrackerResult> GetUpcomingAsync(int days, DateTimeOffset? currentTime = null)
    {
        if (days is < 1 or > 365) throw new CanvasException("Enter a whole number of days between 1 and 365.");
        var now = currentTime ?? DateTimeOffset.UtcNow;
        var courses = await ListAsync<Course>("/api/v1/courses?enrollment_type=student&enrollment_state=active&per_page=100");
        var rows = new List<AssignmentRow>();
        var warnings = new List<string>();
        foreach (var course in courses.Where(course => course.Id > 0 && course.Name is not null))
        {
            try
            {
                var assignments = await ListAsync<CanvasAssignment>($"/api/v1/courses/{course.Id}/assignments?per_page=100");
                foreach (var assignment in assignments)
                {
                    if (assignment.DueAt is not { } due || due < now || due > now.AddDays(days)) continue;
                    var hours = (due - now).TotalHours;
                    rows.Add(new(assignment.Name ?? "Untitled assignment", course.Name!, due,
                        hours <= 24 ? "urgent" : hours <= 72 ? "soon" : "later",
                        hours <= 24 ? "Within 24 hours" : hours <= 72 ? "Within 3 days" : "Later"));
                }
            }
            catch (CanvasException error) { warnings.Add($"{course.Name}: {error.Message}"); }
        }
        return new(rows.OrderBy(row => row.Due).ToList(), warnings);
    }
}
