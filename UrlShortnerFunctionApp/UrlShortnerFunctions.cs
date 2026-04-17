using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace UrlShortnerFunctionApp;

public class UrlShortnerFunctions
{
    private static readonly Dictionary<string, string> UrlMappings = new Dictionary<string, string>();

    // This function handles the URL shortening logic. 
    [Function("ShortenUrl")]
    public static async Task<HttpResponseData> ShortenUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shorten")] HttpRequestData req,
        FunctionContext executionContext)
    {
        //Step 1: Read the long URL from the request body

        var longLink = await req.ReadAsStringAsync(); // Read the long URL from the request body

        //Step 2: Generate a unique short code

        if (string.IsNullOrEmpty(longLink))
        {
            var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest); // Return a bad request response if the long URL is missing
            await badResponse.WriteStringAsync("Please provide a valid URL in the request body.");
            return badResponse;
        }

        // Step 3: Store the mapping of the short code to the long URL

        var code = Guid.NewGuid().ToString().Substring(0, 6); // Generate a unique short code (using the first 6 characters of a GUID)

        UrlMappings[code] = longLink; // Store the mapping of the short code to the long URL

        // Step 4: Return the short URL to the client

        var host = req.Url.GetLeftPart(UriPartial.Authority); // Get the host part of the request URL

        string shortUrl = $"{host}/api/r/{code}"; // Construct the short URL using the host and the short code

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK); // Create a response with status code 200 OK

        await response.WriteStringAsync(shortUrl); // Write the short URL to the response body

        return response; // Return the response to the client
    }

    // This function handles the redirection logic when a short URL is accessed.
    [Function("RedirectToLongUrl")]

    public static async Task<HttpResponseData> RedirectToLongUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "r/{code}")] HttpRequestData req,
        string code,
        FunctionContext executionContext)
    {
        if (UrlMappings.TryGetValue(code, out var longLink)) // Check if the short code exists in the mappings
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect); // Create a response with status code 302 Redirect
            response.Headers.Add("Location", longLink); // Set the Location header to the long URL for redirection
            return response; // Return the redirect response to the client
        }
        else
        {
            var notFoundResponse = req.CreateResponse(System.Net.HttpStatusCode.NotFound); // Create a response with status code 404 Not Found if the short code does not exist
            await notFoundResponse.WriteStringAsync("Short URL not found."); // Write an error message to the response body
            return notFoundResponse; // Return the not found response to the client
        }
    }
}