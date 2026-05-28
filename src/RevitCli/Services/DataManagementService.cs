using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;

namespace RevitCli.Services;

public record CloudModelIds(string Region, string ProjectGuid, string ModelGuid);

public class DataManagementService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public DataManagementService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<CloudModelIds> ResolveAsync(string folderUrl, string modelName, string threeLeggedToken)
    {
        var (projectId, folderId) = ParseFolderUrl(folderUrl);

        var client = _httpClientFactory.CreateClient("aps");

        var (hubId, region) = await FindHubForProjectAsync(client, projectId, threeLeggedToken);

        var itemId = await FindModelInFolderAsync(client, hubId, folderId, modelName, threeLeggedToken);

        var (projectGuid, modelGuid) = await GetModelGuidsFromTipAsync(client, hubId, itemId, threeLeggedToken);

        return new CloudModelIds(region, projectGuid, modelGuid);
    }

    internal static (string ProjectId, string FolderId) ParseFolderUrl(string folderUrl)
    {
        var uri = new Uri(folderUrl);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        string? projectId = null;
        string? folderId = null;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("projects", StringComparison.OrdinalIgnoreCase))
                projectId = segments[i + 1];

            if (segments[i].Equals("folders", StringComparison.OrdinalIgnoreCase))
                folderId = HttpUtility.UrlDecode(segments[i + 1]);
        }

        if (string.IsNullOrEmpty(projectId))
            throw new InvalidOperationException($"Could not extract project ID from folder URL: {folderUrl}");

        if (string.IsNullOrEmpty(folderId))
            throw new InvalidOperationException($"Could not extract folder ID from folder URL: {folderUrl}");

        return (projectId, folderId);
    }

    private async Task<(string HubId, string Region)> FindHubForProjectAsync(
        HttpClient client, string projectId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/data/v1/hubs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var hubs = doc.RootElement.GetProperty("data");

        foreach (var hub in hubs.EnumerateArray())
        {
            var hubId = hub.GetProperty("id").GetString()!;

            var projectExists = await ProjectExistsInHubAsync(client, hubId, projectId, token);
            if (projectExists)
            {
                var region = hub.GetProperty("attributes")
                    .GetProperty("region")
                    .GetString() ?? "US";

                return (hubId, region);
            }
        }

        throw new InvalidOperationException(
            $"Could not find a hub containing project '{projectId}'. Ensure your 3-legged token has access to the project.");
    }

    private async Task<bool> ProjectExistsInHubAsync(
        HttpClient client, string hubId, string projectId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/data/v1/hubs/{hubId}/projects/{projectId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<string> FindModelInFolderAsync(
        HttpClient client, string hubId, string folderId, string modelName, string token)
    {
        var normalizedSearch = NormalizeModelName(modelName);
        var url = $"/data/v1/projects/{hubId}/folders/{Uri.EscapeDataString(folderId)}/contents";
        var availableNames = new List<string>();

        while (url is not null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var items = doc.RootElement.GetProperty("data");

            foreach (var item in items.EnumerateArray())
            {
                var itemType = item.GetProperty("type").GetString();
                if (itemType != "items")
                    continue;

                var displayName = item.GetProperty("attributes")
                    .GetProperty("displayName")
                    .GetString() ?? string.Empty;

                availableNames.Add(displayName);

                if (NormalizeModelName(displayName).Equals(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                    return item.GetProperty("id").GetString()!;
            }

            url = null;
            if (doc.RootElement.TryGetProperty("links", out var links) &&
                links.TryGetProperty("next", out var next) &&
                next.TryGetProperty("href", out var href))
            {
                url = href.GetString();
            }
        }

        var available = availableNames.Count > 0
            ? string.Join(", ", availableNames.Select(n => $"'{n}'"))
            : "(folder is empty)";

        throw new InvalidOperationException(
            $"Model '{modelName}' not found in folder. Available items: {available}");
    }

    private async Task<(string ProjectGuid, string ModelGuid)> GetModelGuidsFromTipAsync(
        HttpClient client, string hubId, string itemId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/data/v1/projects/{hubId}/items/{Uri.EscapeDataString(itemId)}/tip");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var data = doc.RootElement.GetProperty("data");
        var extensionData = data.GetProperty("attributes")
            .GetProperty("extension")
            .GetProperty("data");

        var projectGuid = extensionData.GetProperty("projectGuid").GetString()
            ?? throw new InvalidOperationException("Response missing projectGuid in tip version.");

        var modelGuid = extensionData.GetProperty("modelGuid").GetString()
            ?? throw new InvalidOperationException("Response missing modelGuid in tip version.");

        return (projectGuid, modelGuid);
    }

    private static string NormalizeModelName(string name)
    {
        return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}
