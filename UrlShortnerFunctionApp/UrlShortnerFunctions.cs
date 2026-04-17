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
}

public class UrlShortnerFunctions
{
    // In memory: short code -> full URL. Resets when the app restarts.
    private static readonly Dictionary<string, string> UrlMappings = new Dictionary<string, string>();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex CustomCodePattern = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    private const int MaxCustomCodeLength = 64;

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

        var longLink = data.Url.Trim();
        var custom = data.CustomCode?.Trim();

        string code;
        if (string.IsNullOrEmpty(custom))
        {
            // No custom code: pick a random short string (same idea as before).
            do
            {
                code = Guid.NewGuid().ToString("N")[..6];
            } while (UrlMappings.ContainsKey(code)); // Very unlikely, but avoid a duplicate key.
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

            if (UrlMappings.ContainsKey(custom))
            {
                // Someone already registered this short code.
                var conflict = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
                await conflict.WriteStringAsync("This short code is already in use.");
                return conflict;
            }

            code = custom;
        }

        UrlMappings[code] = longLink; // Remember this pair for GET /api/r/{code}

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
        if (UrlMappings.TryGetValue(code, out var longLink))
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
            response.Headers.Add("Location", longLink);
            return response;
        }

        var notFoundResponse = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        await notFoundResponse.WriteStringAsync("Short URL not found.");
        return notFoundResponse;
    }
}