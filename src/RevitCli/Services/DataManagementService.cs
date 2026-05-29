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

        var (_, region) = await FindHubForProjectAsync(client, projectId, threeLeggedToken);

        var (projectGuid, modelGuid) = await FindModelInFolderAsync(client, projectId, folderId, modelName, threeLeggedToken);

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

        var query = HttpUtility.ParseQueryString(uri.Query);
        var folderUrnFromQuery = query["folderUrn"];
        if (!string.IsNullOrEmpty(folderUrnFromQuery))
            folderId = folderUrnFromQuery;

        if (!string.IsNullOrEmpty(projectId) && !projectId.StartsWith("b.", StringComparison.OrdinalIgnoreCase))
            projectId = "b." + projectId;

        if (string.IsNullOrEmpty(projectId))
            throw new InvalidOperationException($"Could not extract project ID from folder URL: {folderUrl}");

        if (string.IsNullOrEmpty(folderId))
            throw new InvalidOperationException($"Could not extract folder ID from folder URL: {folderUrl}");

        return (projectId, folderId);
    }

    private async Task<(string HubId, string Region)> FindHubForProjectAsync(
        HttpClient client, string projectId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/project/v1/hubs");
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
        var request = new HttpRequestMessage(HttpMethod.Get, $"/project/v1/hubs/{hubId}/projects/{projectId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<(string ProjectGuid, string ModelGuid)> FindModelInFolderAsync(
        HttpClient client, string projectId, string folderId, string modelName, string token)
    {
        var normalizedSearch = NormalizeModelName(modelName);
        var url = $"/data/v1/projects/{projectId}/folders/{Uri.EscapeDataString(folderId)}/contents";
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

                if (!NormalizeModelName(displayName).Equals(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                var tipVersionId = item.GetProperty("relationships")
                    .GetProperty("tip")
                    .GetProperty("data")
                    .GetProperty("id")
                    .GetString()
                    ?? throw new InvalidOperationException($"Item '{displayName}' has no tip version id.");

                return ExtractGuidsFromIncluded(doc.RootElement, tipVersionId, displayName);
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

    private static (string ProjectGuid, string ModelGuid) ExtractGuidsFromIncluded(
        JsonElement root, string tipVersionId, string displayName)
    {
        if (!root.TryGetProperty("included", out var included) || included.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Response for item '{displayName}' has no 'included' tip versions.");

        foreach (var entry in included.EnumerateArray())
        {
            if (entry.GetProperty("id").GetString() != tipVersionId)
                continue;

            var extensionData = entry.GetProperty("attributes")
                .GetProperty("extension")
                .GetProperty("data");

            var projectGuid = extensionData.GetProperty("projectGuid").GetString()
                ?? throw new InvalidOperationException($"Tip version for '{displayName}' missing projectGuid.");

            var modelGuid = extensionData.GetProperty("modelGuid").GetString()
                ?? throw new InvalidOperationException($"Tip version for '{displayName}' missing modelGuid.");

            return (projectGuid, modelGuid);
        }

        throw new InvalidOperationException(
            $"Tip version '{tipVersionId}' for item '{displayName}' not found in response 'included' array.");
    }

    private static string NormalizeModelName(string name)
    {
        return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}
