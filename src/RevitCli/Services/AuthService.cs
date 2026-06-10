using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RevitCli.Infrastructure;
using RevitCli.Models.Api;

namespace RevitCli.Services;

public class AuthService
{
    private const string RedirectUri = "http://localhost:8080/callback";
    private const string TwoLeggedScopes = "code:all data:read data:write bucket:create bucket:delete bucket:read";
    private const string ThreeLeggedScopes = "code:all data:read data:write";
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenStore _tokenStore;

    public AuthService(IHttpClientFactory httpClientFactory, TokenStore tokenStore)
    {
        _httpClientFactory = httpClientFactory;
        _tokenStore = tokenStore;
    }

    public async Task<string> GetTwoLeggedTokenAsync(string clientId, string clientSecret)
    {
        var client = _httpClientFactory.CreateClient("aps");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        var request = new HttpRequestMessage(HttpMethod.Post, "/authentication/v2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = TwoLeggedScopes
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync("2-legged token");

        var tokenResponse = await JsonSerializer.DeserializeAsync<TokenResponse>(
            await response.Content.ReadAsStreamAsync())
            ?? throw new InvalidOperationException("Failed to deserialize token response.");

        return tokenResponse.AccessToken;
    }

    public async Task<string> EnsureThreeLeggedTokenAsync(string clientId, string clientSecret)
    {
        try
        {
            return await _tokenStore.GetValidThreeLeggedTokenAsync(clientId, clientSecret);
        }
        catch (InvalidOperationException)
        {
            return await RunBrowserAuthFlowAsync(clientId, clientSecret);
        }
    }

    private async Task<string> RunBrowserAuthFlowAsync(string clientId, string clientSecret)
    {
        var encodedRedirect = Uri.EscapeDataString(RedirectUri);
        var encodedScopes = Uri.EscapeDataString(ThreeLeggedScopes);
        var authorizeUrl = $"https://developer.api.autodesk.com/authentication/v2/authorize" +
            $"?response_type=code&client_id={clientId}&redirect_uri={encodedRedirect}&scope={encodedScopes}";

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:8080/callback/");
        listener.Start();

        OpenBrowser(authorizeUrl);

        var code = await WaitForAuthorizationCodeAsync(listener);

        var accessToken = await ExchangeCodeAsync(clientId, clientSecret, code);
        return accessToken;
    }

    private static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static async Task<string> WaitForAuthorizationCodeAsync(HttpListener listener)
    {
        using var cts = new CancellationTokenSource(LoginTimeout);

        var getContextTask = listener.GetContextAsync();
        var completedTask = await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, cts.Token));

        if (completedTask != getContextTask)
            throw new TimeoutException("Browser login timed out after 5 minutes.");

        var context = await getContextTask;
        var code = context.Request.QueryString["code"]
            ?? throw new InvalidOperationException("No authorization code received in callback.");

        var responseBytes = Encoding.UTF8.GetBytes(
            "<html><body><h2>Login successful!</h2><p>You can close this window and return to the CLI.</p></body></html>");
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();

        return code;
    }

    private async Task<string> ExchangeCodeAsync(string clientId, string clientSecret, string code)
    {
        var client = _httpClientFactory.CreateClient("aps");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        var request = new HttpRequestMessage(HttpMethod.Post, "/authentication/v2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync("authorization code exchange");

        var tokenResponse = await JsonSerializer.DeserializeAsync<TokenResponse>(
            await response.Content.ReadAsStreamAsync())
            ?? throw new InvalidOperationException("Failed to deserialize token response.");

        await _tokenStore.SaveTokenAsync(
            tokenResponse.AccessToken,
            tokenResponse.RefreshToken,
            tokenResponse.ExpiresIn,
            clientId,
            clientSecret);

        return tokenResponse.AccessToken;
    }
}
