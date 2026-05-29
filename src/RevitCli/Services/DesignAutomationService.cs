using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RevitCli.Infrastructure;
using RevitCli.Models.Api;

namespace RevitCli.Services;

public class DesignAutomationService
{
    private const string DaBasePath = "/da/us-east/v3";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppStateStore _appStateStore;

    public DesignAutomationService(IHttpClientFactory httpClientFactory, AppStateStore appStateStore)
    {
        _httpClientFactory = httpClientFactory;
        _appStateStore = appStateStore;
    }

    public async Task<string> GetNicknameAsync(string twoLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");
        var request = new HttpRequestMessage(HttpMethod.Get, $"{DaBasePath}/forgeapps/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync("get DA nickname");

        var content = await response.Content.ReadAsStringAsync();
        return content.Trim('"');
    }

    public async Task<(int Version, bool WasSkipped)> EnsureAppBundleAsync(
        string appName,
        string engineId,
        string zipPath,
        string zipHash,
        string nickname,
        string twoLeggedToken)
    {
        var state = await _appStateStore.GetAppStateAsync(appName);

        if (state is not null && state.ZipHash == zipHash)
            return (state.AppBundleVersion, WasSkipped: true);

        var version = state?.AppBundleVersion ?? 0;
        var client = _httpClientFactory.CreateClient("aps");
        var qualifiedId = $"{nickname}.{appName}";

        AppBundleResponse bundleResponse;
        if (version == 0)
        {
            bundleResponse = await CreateAppBundleAsync(client, appName, engineId, twoLeggedToken);
        }
        else
        {
            bundleResponse = await UpdateAppBundleAsync(client, qualifiedId, engineId, twoLeggedToken);
        }

        var newVersion = ExtractVersion(bundleResponse.Id);
        await UploadZipToS3Async(bundleResponse.UploadParameters!, zipPath);
        await EnsureAliasAsync(client, $"{DaBasePath}/appbundles/{appName}/aliases", "prod", newVersion, version == 0, twoLeggedToken);
        await _appStateStore.SaveAppStateAsync(appName, newVersion, zipHash);

        return (newVersion, WasSkipped: false);
    }

    public async Task<string> EnsureActivityAsync(
        string appName,
        string engineId,
        string nickname,
        string twoLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");
        var activityId = $"{appName}-activity";
        var qualifiedAppBundle = $"{nickname}.{appName}+prod";
        var qualifiedActivityId = $"{nickname}.{activityId}";

        var commandLine = $"$(engine.path)\\\\revitcoreconsole.exe /i \"$(args[inputFile].path)\" /al \"$(appbundles[{appName}].path)\"";

        var activityPayload = new
        {
            id = activityId,
            engine = engineId,
            commandLine = new[] { commandLine },
            appbundles = new[] { qualifiedAppBundle },
            parameters = new Dictionary<string, object>
            {
                ["inputFile"] = new { verb = "get", required = true },
                ["inputParams"] = new { verb = "get", localName = "params.json" },
                ["outputFile"] = new { verb = "put", required = true }
            }
        };

        var json = JsonSerializer.Serialize(activityPayload);
        ActivityResponse activityResponse;

        var existsRequest = new HttpRequestMessage(HttpMethod.Get, $"{DaBasePath}/activities/{qualifiedActivityId}");
        existsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);
        var existsResponse = await client.SendAsync(existsRequest);

        if (existsResponse.StatusCode == HttpStatusCode.NotFound)
        {
            var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{DaBasePath}/activities")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

            var createResponse = await client.SendAsync(createRequest);
            await createResponse.EnsureSuccessOrThrowAsync("create activity");

            activityResponse = await JsonSerializer.DeserializeAsync<ActivityResponse>(
                await createResponse.Content.ReadAsStreamAsync())
                ?? throw new InvalidOperationException("Failed to deserialize activity response.");

            await EnsureAliasAsync(client, $"{DaBasePath}/activities/{activityId}/aliases", "prod", 1, true, twoLeggedToken);
        }
        else
        {
            await existsResponse.EnsureSuccessOrThrowAsync("get activity");

            var updateRequest = new HttpRequestMessage(HttpMethod.Patch, $"{DaBasePath}/activities/{qualifiedActivityId}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            updateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

            var updateResponse = await client.SendAsync(updateRequest);
            await updateResponse.EnsureSuccessOrThrowAsync("update activity");

            activityResponse = await JsonSerializer.DeserializeAsync<ActivityResponse>(
                await updateResponse.Content.ReadAsStreamAsync())
                ?? throw new InvalidOperationException("Failed to deserialize activity response.");

            var newVersion = ExtractVersion(activityResponse.Id);
            await EnsureAliasAsync(client, $"{DaBasePath}/activities/{activityId}/aliases", "prod", newVersion, false, twoLeggedToken);
        }

        return $"{nickname}.{activityId}+prod";
    }

    public async Task<string> SubmitWorkItemAsync(
        string activityId,
        string modelDataJson,
        string outputBucketUrn,
        string twoLeggedToken,
        string threeLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");

        var workItemPayload = new
        {
            activityId,
            arguments = new Dictionary<string, object>
            {
                ["inputFile"] = new
                {
                    url = modelDataJson,
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {threeLeggedToken}"
                    },
                    verb = "get"
                },
                ["inputParams"] = new
                {
                    url = "data:application/json," + modelDataJson,
                    verb = "get"
                },
                ["outputFile"] = new
                {
                    url = outputBucketUrn,
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {twoLeggedToken}"
                    },
                    verb = "put"
                }
            }
        };

        var json = JsonSerializer.Serialize(workItemPayload);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{DaBasePath}/workitems")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync("submit work item");

        var workItemResponse = await JsonSerializer.DeserializeAsync<WorkItemResponse>(
            await response.Content.ReadAsStreamAsync())
            ?? throw new InvalidOperationException("Failed to deserialize work item response.");

        return workItemResponse.Id;
    }

    public async Task<WorkItemResponse> PollWorkItemAsync(
        string workItemId,
        string twoLeggedToken,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("aps");

        while (!ct.IsCancellationRequested)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{DaBasePath}/workitems/{workItemId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

            var response = await client.SendAsync(request, ct);
            await response.EnsureSuccessOrThrowAsync("poll work item", ct);

            var workItem = await JsonSerializer.DeserializeAsync<WorkItemResponse>(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct)
                ?? throw new InvalidOperationException("Failed to deserialize work item response.");

            if (workItem.Status is "success" or "failed" or "cancelled" or "failedLimitProcessingTime"
                or "failedLimitDataSize" or "failedDownload" or "failedInstructions" or "failedUpload")
            {
                return workItem;
            }

            await Task.Delay(PollInterval, ct);
        }

        throw new OperationCanceledException(ct);
    }

    private static async Task<AppBundleResponse> CreateAppBundleAsync(
        HttpClient client, string appName, string engineId, string token)
    {
        var payload = new { id = appName, engine = engineId };
        var json = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{DaBasePath}/appbundles")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync("create app bundle");

        return await JsonSerializer.DeserializeAsync<AppBundleResponse>(
            await response.Content.ReadAsStreamAsync())
            ?? throw new InvalidOperationException("Failed to deserialize app bundle response.");
    }

    private static async Task<AppBundleResponse> UpdateAppBundleAsync(
        HttpClient client, string qualifiedId, string engineId, string token)
    {
        var payload = new { engine = engineId };
        var json = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{DaBasePath}/appbundles/{qualifiedId}/versions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync("create app bundle version");

        return await JsonSerializer.DeserializeAsync<AppBundleResponse>(
            await response.Content.ReadAsStreamAsync())
            ?? throw new InvalidOperationException("Failed to deserialize app bundle response.");
    }

    private static async Task UploadZipToS3Async(UploadParameters uploadParams, string zipPath)
    {
        using var formContent = new MultipartFormDataContent();

        foreach (var kvp in uploadParams.FormData)
        {
            formContent.Add(new StringContent(kvp.Value), kvp.Key);
        }

        var zipBytes = await File.ReadAllBytesAsync(zipPath);
        var zipContent = new ByteArrayContent(zipBytes);
        zipContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        formContent.Add(zipContent, "file", Path.GetFileName(zipPath));

        using var httpClient = new HttpClient();
        var response = await httpClient.PostAsync(uploadParams.EndpointUrl, formContent);
        await response.EnsureSuccessOrThrowAsync("upload app bundle zip to S3");
    }

    private static async Task EnsureAliasAsync(
        HttpClient client, string aliasBasePath, string aliasName, int version, bool isNew, string token)
    {
        if (isNew)
        {
            var payload = new { id = aliasName, version };
            var json = JsonSerializer.Serialize(payload);

            var request = new HttpRequestMessage(HttpMethod.Post, aliasBasePath)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            await response.EnsureSuccessOrThrowAsync("create alias");
        }
        else
        {
            var payload = new { version };
            var json = JsonSerializer.Serialize(payload);

            var request = new HttpRequestMessage(HttpMethod.Patch, $"{aliasBasePath}/{aliasName}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            await response.EnsureSuccessOrThrowAsync("update alias");
        }
    }

    private static int ExtractVersion(string id)
    {
        var parts = id.Split('+');
        if (parts.Length >= 2 && int.TryParse(parts[^1], out var version))
            return version;

        var dollarParts = id.Split('$');
        if (dollarParts.Length >= 2 && int.TryParse(dollarParts[^1], out var v))
            return v;

        return 1;
    }
}
