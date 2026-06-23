using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;
using RevitCli.Infrastructure;

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

    public async Task<List<(string DisplayName, CloudModelIds Ids)>> ResolveAllAsync(
        string folderUrl, string threeLeggedToken)
    {
        var (projectId, folderId) = ParseFolderUrl(folderUrl);
        var client = _httpClientFactory.CreateClient("aps");
        var (_, region) = await FindHubForProjectAsync(client, projectId, threeLeggedToken);
        return await FindAllModelsInFolderAsync(client, projectId, folderId, region, threeLeggedToken);
    }

    public async Task<List<(string DisplayName, CloudModelIds Ids)>> ResolveMultipleByNameAsync(
        string folderUrl, List<string> modelNames, string threeLeggedToken)
    {
        var (projectId, folderId) = ParseFolderUrl(folderUrl);
        var client = _httpClientFactory.CreateClient("aps");
        var (_, region) = await FindHubForProjectAsync(client, projectId, threeLeggedToken);
        return await FindModelsByNameAsync(client, projectId, folderId, modelNames, region, threeLeggedToken);
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
        await response.EnsureSuccessOrThrowAsync("list hubs");

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
            await response.EnsureSuccessOrThrowAsync("list folder contents");

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

    private async Task<List<(string DisplayName, CloudModelIds Ids)>> FindAllModelsInFolderAsync(
        HttpClient client, string projectId, string folderId, string region, string token)
    {
        var results = new List<(string DisplayName, CloudModelIds Ids)>();
        var url = (string?)$"/data/v1/projects/{projectId}/folders/{Uri.EscapeDataString(folderId)}/contents";

        while (url is not null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            await response.EnsureSuccessOrThrowAsync("list folder contents");

            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

            if (doc.RootElement.TryGetProperty("included", out var versions) &&
                versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versions.EnumerateArray())
                {
                    if (version.GetProperty("type").GetString() != "versions")
                        continue;

                    if (!IsRevitCloudModel(version))
                        continue;

                    var displayName = version.GetProperty("attributes")
                        .GetProperty("displayName")
                        .GetString() ?? string.Empty;

                    var extensionData = version.GetProperty("attributes")
                        .GetProperty("extension")
                        .GetProperty("data");

                    if (!extensionData.TryGetProperty("projectGuid", out var projectGuidEl) ||
                        !extensionData.TryGetProperty("modelGuid", out var modelGuidEl))
                        continue;

                    var projectGuid = projectGuidEl.GetString();
                    var modelGuid = modelGuidEl.GetString();
                    if (projectGuid is null || modelGuid is null)
                        continue;

                    results.Add((displayName, new CloudModelIds(region, projectGuid, modelGuid)));
                }
            }

            url = null;
            if (doc.RootElement.TryGetProperty("links", out var links) &&
                links.TryGetProperty("next", out var next) &&
                next.TryGetProperty("href", out var href))
            {
                url = href.GetString();
            }
        }

        return results;
    }

    private async Task<List<(string DisplayName, CloudModelIds Ids)>> FindModelsByNameAsync(
        HttpClient client, string projectId, string folderId, List<string> modelNames,
        string region, string token)
    {
        var remaining = new HashSet<string>(
            modelNames.Select(NormalizeModelName), StringComparer.OrdinalIgnoreCase);
        var results = new List<(string DisplayName, CloudModelIds Ids)>();
        var availableNames = new List<string>();
        var url = (string?)$"/data/v1/projects/{projectId}/folders/{Uri.EscapeDataString(folderId)}/contents";

        while (url is not null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            await response.EnsureSuccessOrThrowAsync("list folder contents");

            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var items = doc.RootElement.GetProperty("data");

            foreach (var item in items.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() != "items")
                    continue;

                var displayName = item.GetProperty("attributes")
                    .GetProperty("displayName")
                    .GetString() ?? string.Empty;

                availableNames.Add(displayName);
                var normalized = NormalizeModelName(displayName);

                if (!remaining.Contains(normalized))
                    continue;

                var tipVersionId = item.GetProperty("relationships")
                    .GetProperty("tip")
                    .GetProperty("data")
                    .GetProperty("id")
                    .GetString();

                if (tipVersionId is null)
                    continue;

                var (projectGuid, modelGuid) = ExtractGuidsFromIncluded(doc.RootElement, tipVersionId, displayName);
                results.Add((displayName, new CloudModelIds(region, projectGuid, modelGuid)));
                remaining.Remove(normalized);
            }

            url = null;
            if (doc.RootElement.TryGetProperty("links", out var links) &&
                links.TryGetProperty("next", out var next) &&
                next.TryGetProperty("href", out var href))
            {
                url = href.GetString();
            }
        }

        if (remaining.Count > 0)
        {
            var missing = string.Join(", ", remaining.Select(n => $"'{n}'"));
            var available = availableNames.Count > 0
                ? string.Join(", ", availableNames.Select(n => $"'{n}'"))
                : "(folder is empty)";
            throw new InvalidOperationException(
                $"Model(s) not found in folder: {missing}. Available items: {available}");
        }

        return results;
    }

    private static bool IsRevitCloudModel(JsonElement version)
    {
        if (!version.TryGetProperty("attributes", out var attributes) ||
            !attributes.TryGetProperty("extension", out var extension) ||
            !extension.TryGetProperty("type", out var typeEl))
            return false;

        var extensionType = typeEl.GetString() ?? string.Empty;
        return extensionType.Contains("C4RModel", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModelName(string name)
    {
        return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}
