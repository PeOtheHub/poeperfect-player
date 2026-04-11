using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace APTV.Services;

internal static class BrowserHttpClient
{
    private static readonly AppLogger Logger = AppLogger.Instance;
    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static HttpClient SharedClient { get; } = CreateClient();

    public static HttpRequestMessage CreateGetRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) PoePerfectPlayer/1.0");
        request.Headers.Accept.ParseAdd("application/json, application/x-mpegURL, application/vnd.apple.mpegurl, audio/x-mpegurl, text/plain, */*");
        request.Headers.AcceptLanguage.ParseAdd("sv-SE, sv;q=0.9, en-US;q=0.8, en;q=0.7");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.ConnectionClose = true;

        if (!string.IsNullOrWhiteSpace(uri.Host))
        {
            request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority));
        }

        return request;
    }

    public static async Task<HttpResponseMessage> SendGetAsync(
        Uri uri,
        string context,
        CancellationToken cancellationToken)
    {
        using var request = CreateGetRequest(uri);
        Logger.Info($"{context}: sending GET request. Uri={Logger.DescribeSource(uri.ToString())}");

        var response = await SharedClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        Logger.Info($"{context}: response {(int)response.StatusCode} {response.ReasonPhrase}. Uri={Logger.DescribeSource(uri.ToString())}");

        if (!response.IsSuccessStatusCode)
        {
            var errorSnippet = await TryReadErrorSnippetAsync(response, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(errorSnippet))
            {
                Logger.Warning($"{context}: error body snippet: {errorSnippet}");
            }
        }

        return response;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            AllowAutoRedirect = true,
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
    }

    private static async Task<string?> TryReadErrorSnippetAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = response.Content;
            if (content is null)
            {
                return null;
            }

            var body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            body = CollapseWhitespaceRegex.Replace(body, " ").Trim();
            if (body.Length > 400)
            {
                body = body[..400];
            }

            return Logger.SanitizeSensitiveText(body);
        }
        catch (Exception exception)
        {
            Logger.Warning($"Failed to read error response body snippet: {exception.Message}");
            return null;
        }
    }
}
