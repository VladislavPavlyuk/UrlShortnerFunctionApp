using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace UrlShortnerFunctionApp;

// Matches the JSON body from POST /api/shorten (property names can be any casing).
public class ShortenRequest
{
    public string Url { get; set; } = string.Empty;
    public string? CustomCode { get; set; }

    /// <summary>Optional. After this moment (UTC) the short link answers with 410 Gone.</summary>
    public DateTime? ExpiresAt { get; set; }
}

// One row in our in-memory store.
public class ShortLinkEntry
{
    public string LongUrl { get; set; } = string.Empty;

    /// <summary>null = never expires</summary>
    public DateTime? ExpiresAt { get; set; }
}

public class UrlShortnerFunctions
{
    // In memory: short code -> entry. Resets when the app restarts.
    private static readonly Dictionary<string, ShortLinkEntry> UrlStorage = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex CustomCodePattern = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    private const int MaxCustomCodeLength = 64;

    private static bool IsExpired(ShortLinkEntry entry) =>
        entry.ExpiresAt is { } exp && exp <= DateTime.UtcNow;

    [Function("ShortenUrl")]
    public static async Task<HttpResponseData> ShortenUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shorten")] HttpRequestData req,
        FunctionContext executionContext)
    {
        var requestBody = await req.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(requestBody))
        {
            var emptyBody = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await emptyBody.WriteStringAsync("Please provide a JSON body with a \"url\" field.");
            return emptyBody;
        }

        ShortenRequest? data;
        try
        {
            // Turn the JSON text into a C# object we can use.
            data = JsonSerializer.Deserialize<ShortenRequest>(requestBody, JsonOptions);
        }
        catch (JsonException)
        {
            var invalidJson = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await invalidJson.WriteStringAsync("Invalid JSON.");
            return invalidJson;
        }

        if (data is null || string.IsNullOrWhiteSpace(data.Url))
        {
            var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("Please provide a valid URL in the request body.");
            return badResponse;
        }

        if (data.ExpiresAt is { } deadline && deadline <= DateTime.UtcNow)
        {
            var badExp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badExp.WriteStringAsync("expiresAt must be in the future (UTC).");
            return badExp;
        }

        var longLink = data.Url.Trim();
        var custom = data.CustomCode?.Trim();

        string code;
        if (string.IsNullOrEmpty(custom))
        {
            // No custom code: pick a random short string (same idea as before).
            do
            {
                code = Guid.NewGuid().ToString("N")[..6];
            } while (UrlStorage.ContainsKey(code)); // Very unlikely, but avoid a duplicate key.
        }
        else
        {
            // Custom code must be safe to put in a URL path (letters, numbers, _ and -).
            if (custom.Length > MaxCustomCodeLength || !CustomCodePattern.IsMatch(custom))
            {
                var invalidCode = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await invalidCode.WriteStringAsync(
                    $"Custom code must be 1–{MaxCustomCodeLength} characters and contain only letters, digits, underscore or hyphen.");
                return invalidCode;
            }

            if (UrlStorage.ContainsKey(custom))
            {
                // Someone already registered this short code.
                var conflict = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
                await conflict.WriteStringAsync("This short code is already in use.");
                return conflict;
            }

            code = custom;
        }

        UrlStorage[code] = new ShortLinkEntry { LongUrl = longLink, ExpiresAt = data.ExpiresAt };

        var host = req.Url.GetLeftPart(UriPartial.Authority);
        string shortUrl = $"{host}/api/r/{code}";

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteStringAsync(shortUrl);
        return response;
    }

    // GET /api/r/{code} — send the browser to the saved long URL.
    [Function("RedirectToLongUrl")]
    public static async Task<HttpResponseData> RedirectToLongUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "r/{code}")] HttpRequestData req,
        string code,
        FunctionContext executionContext)
    {
        if (!UrlStorage.TryGetValue(code, out var entry))
        {
            var notFoundResponse = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
            await notFoundResponse.WriteStringAsync("Short URL not found.");
            return notFoundResponse;
        }

        if (IsExpired(entry))
        {
            var gone = req.CreateResponse(System.Net.HttpStatusCode.Gone);
            await gone.WriteStringAsync("This short link has expired.");
            return gone;
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
        response.Headers.Add("Location", entry.LongUrl);
        return response;
    }

    // DELETE /api/delete/{code} — remove a short code from storage.
    [Function("DeleteShortUrl")]
    public static async Task<HttpResponseData> DeleteShortUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "delete/{code}")] HttpRequestData req,
        string code,
        FunctionContext executionContext)
    {
        if (!UrlStorage.Remove(code))
        {
            var notFound = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Short URL not found.");
            return notFound;
        }

        var ok = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await ok.WriteStringAsync($"Short code \"{code}\" was deleted.");
        return ok;
    }
}
