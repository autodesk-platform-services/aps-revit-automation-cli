using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitCli.Models.Api;

namespace RevitCli.Infrastructure;

public class TokenStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revit-cli");

    private static readonly string TokensPath = Path.Combine(ConfigDir, "tokens.json");

    private readonly IHttpClientFactory _httpClientFactory;

    public TokenStore(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetValidThreeLeggedTokenAsync(string clientId, string clientSecret)
    {
        var stored = await LoadAsync();
        if (stored is null)
            throw new InvalidOperationException(
                "No stored token found. Run 'revit auth login' first.");

        if (stored.ExpiresAtUtc > DateTime.UtcNow.AddSeconds(60))
            return stored.AccessToken;

        if (string.IsNullOrEmpty(stored.RefreshToken))
            throw new InvalidOperationException(
                "Token expired and no refresh token available. Run 'revit auth login' again.");

        var client = _httpClientFactory.CreateClient("aps");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        var request = new HttpRequestMessage(HttpMethod.Post, "/authentication/v2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = stored.RefreshToken
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync("refresh 3-legged token");

        var tokenResponse = await JsonSerializer.DeserializeAsync<TokenResponse>(
            await response.Content.ReadAsStreamAsync())
            ?? throw new InvalidOperationException("Failed to deserialize token response.");

        var entry = new StoredToken
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken ?? stored.RefreshToken,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
            ClientId = stored.ClientId,
            ClientSecret = stored.ClientSecret
        };

        await SaveAsync(entry);
        return entry.AccessToken;
    }

    public async Task SaveTokenAsync(string accessToken, string? refreshToken, int expiresIn, string clientId, string clientSecret)
    {
        var entry = new StoredToken
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn),
            ClientId = clientId,
            ClientSecret = clientSecret
        };

        await SaveAsync(entry);
    }

    public async Task<(string ClientId, string ClientSecret)> GetCredentialsAsync()
    {
        var stored = await LoadAsync();
        if (stored is null || string.IsNullOrWhiteSpace(stored.ClientId) || string.IsNullOrWhiteSpace(stored.ClientSecret))
            throw new InvalidOperationException(
                "No stored credentials found. Run 'revit auth login' first.");

        return (stored.ClientId, stored.ClientSecret);
    }

    public async Task<StoredToken?> LoadAsync()
    {
        if (!File.Exists(TokensPath))
            return null;

        var json = await File.ReadAllTextAsync(TokensPath);
        return JsonSerializer.Deserialize<StoredToken>(json);
    }

    private static async Task SaveAsync(StoredToken token)
    {
        Directory.CreateDirectory(ConfigDir);

        var json = JsonSerializer.Serialize(token, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = TokensPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, TokensPath, overwrite: true);
    }

    public class StoredToken
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expiresAtUtc")]
        public DateTime ExpiresAtUtc { get; set; }

        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }

        [JsonPropertyName("clientSecret")]
        public string? ClientSecret { get; set; }
    }
}
