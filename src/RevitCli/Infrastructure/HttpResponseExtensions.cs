namespace RevitCli.Infrastructure;

public class ApsHttpException : HttpRequestException
{
    public ApsHttpException(string message) : base(message) { }
}

public static class HttpResponseExtensions
{
    private const int MaxBodyChars = 4000;

    public static async Task EnsureSuccessOrThrowAsync(
        this HttpResponseMessage response,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
            return;

        var method = response.RequestMessage?.Method?.Method ?? "?";
        var url = response.RequestMessage?.RequestUri?.ToString() ?? "?";
        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? response.StatusCode.ToString();

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            body = $"<failed to read response body: {ex.Message}>";
        }

        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars] + "... [truncated]";

        var header = string.IsNullOrEmpty(context)
            ? $"APS request failed: {method} {url} -> {status} {reason}"
            : $"APS request failed [{context}]: {method} {url} -> {status} {reason}";

        var message = string.IsNullOrWhiteSpace(body)
            ? header
            : $"{header}\nResponse body:\n{body}";

        throw new ApsHttpException(message);
    }
}
