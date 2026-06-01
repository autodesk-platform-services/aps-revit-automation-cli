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
        string aliasName,
        string twoLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");
        var qualifiedId = $"{nickname}.{appName}";
        var aliasBasePath = $"{DaBasePath}/appbundles/{appName}/aliases";
        var state = await _appStateStore.GetAppStateAsync(appName);

        if (state is not null && state.ZipHash == zipHash)
        {
            if (await AppBundleAliasExistsAsync(client, qualifiedId, aliasName, twoLeggedToken))
                return (state.AppBundleVersion, WasSkipped: true);

            if (await AppBundleEntityExistsAsync(client, appName, twoLeggedToken))
            {
                await EnsureAliasAsync(client, aliasBasePath, aliasName, state.AppBundleVersion, isNew: true, twoLeggedToken);
                return (state.AppBundleVersion, WasSkipped: true);
            }

            state = null;
        }

        var bundleEntityExists = await AppBundleEntityExistsAsync(client, appName, twoLeggedToken);

        AppBundleResponse bundleResponse = bundleEntityExists
            ? await UpdateAppBundleAsync(client, qualifiedId, engineId, twoLeggedToken)
            : await CreateAppBundleAsync(client, appName, engineId, twoLeggedToken);

        var newVersion = ExtractVersion(bundleResponse.Id);
        await UploadZipToS3Async(bundleResponse.UploadParameters!, zipPath);

        var aliasExists = await AppBundleAliasExistsAsync(client, qualifiedId, aliasName, twoLeggedToken);
        await EnsureAliasAsync(client, aliasBasePath, aliasName, newVersion, isNew: !aliasExists, twoLeggedToken);

        await _appStateStore.SaveAppStateAsync(appName, newVersion, zipHash);

        return (newVersion, WasSkipped: false);
    }

    public async Task<string> EnsureActivityAsync(
        string appName,
        string engineId,
        string nickname,
        string aliasName,
        string twoLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");
        var activityId = $"{appName}Activity";
        var qualifiedAppBundle = $"{nickname}.{appName}+{aliasName}";
        var aliasBasePath = $"{DaBasePath}/activities/{activityId}/aliases";

        var commandLine = $"$(engine.path)\\\\revitcoreconsole.exe /al \"$(appbundles[{appName}].path)\"";

        var parameters = new Dictionary<string, object>
        {
            ["revitmodel"] = new { verb = "get", localName = "revitmodel.json" },
            ["toolinputs"] = new { verb = "get", localName = "toolinputs.json" },
            ["result"] = new { verb = "put", localName = "result.json" }
        };

        var createPayload = new
        {
            id = activityId,
            engine = engineId,
            commandLine = new[] { commandLine },
            appbundles = new[] { qualifiedAppBundle },
            parameters
        };

        var newVersionPayload = new
        {
            engine = engineId,
            commandLine = new[] { commandLine },
            appbundles = new[] { qualifiedAppBundle },
            parameters
        };

        var entityExists = await ActivityEntityExistsAsync(client, activityId, twoLeggedToken);
        ActivityResponse activityResponse;

        if (entityExists)
        {
            var versionJson = JsonSerializer.Serialize(newVersionPayload);
            var versionRequest = new HttpRequestMessage(HttpMethod.Post, $"{DaBasePath}/activities/{activityId}/versions")
            {
                Content = new StringContent(versionJson, Encoding.UTF8, "application/json")
            };
            versionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

            var versionResponse = await client.SendAsync(versionRequest);
            await versionResponse.EnsureSuccessOrThrowAsync("create activity version");

            activityResponse = await JsonSerializer.DeserializeAsync<ActivityResponse>(
                await versionResponse.Content.ReadAsStreamAsync())
                ?? throw new InvalidOperationException("Failed to deserialize activity response.");
        }
        else
        {
            var createJson = JsonSerializer.Serialize(createPayload);
            var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{DaBasePath}/activities")
            {
                Content = new StringContent(createJson, Encoding.UTF8, "application/json")
            };
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

            var createResponse = await client.SendAsync(createRequest);
            await createResponse.EnsureSuccessOrThrowAsync("create activity");

            activityResponse = await JsonSerializer.DeserializeAsync<ActivityResponse>(
                await createResponse.Content.ReadAsStreamAsync())
                ?? throw new InvalidOperationException("Failed to deserialize activity response.");
        }

        var newVersion = ExtractVersion(activityResponse.Id);
        var aliasExists = await ActivityAliasExistsAsync(client, $"{nickname}.{activityId}", aliasName, twoLeggedToken);
        await EnsureAliasAsync(client, aliasBasePath, aliasName, newVersion, isNew: !aliasExists, twoLeggedToken);

        return $"{nickname}.{activityId}+{aliasName}";
    }

    public async Task<string> SubmitWorkItemAsync(
        string activityId,
        string cloudModelJson,
        string toolInputsJson,
        string? outputBucketUrn,
        string twoLeggedToken,
        string threeLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");

        var arguments = new Dictionary<string, object>
        {
            ["revitmodel"] = new
            {
                url = "data:application/json," + cloudModelJson,
                verb = "get"
            },
            ["toolinputs"] = new
            {
                url = "data:application/json," + toolInputsJson,
                verb = "get"
            },
            ["adsk3LeggedToken"] = threeLeggedToken
        };

        if (!string.IsNullOrEmpty(outputBucketUrn))
        {
            arguments["result"] = new
            {
                url = outputBucketUrn,
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {twoLeggedToken}"
                },
                verb = "put"
            };
        }

        var workItemPayload = new
        {
            activityId,
            arguments
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

    private static async Task<bool> AppBundleAliasExistsAsync(
        HttpClient client, string qualifiedId, string alias, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{DaBasePath}/appbundles/{qualifiedId}+{alias}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        await response.EnsureSuccessOrThrowAsync("check app bundle alias");
        return true;
    }

    private static async Task<bool> AppBundleEntityExistsAsync(
        HttpClient client, string appName, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{DaBasePath}/appbundles/{appName}/aliases");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        await response.EnsureSuccessOrThrowAsync("check app bundle entity");
        return true;
    }

    private static async Task<bool> ActivityEntityExistsAsync(
        HttpClient client, string activityId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{DaBasePath}/activities/{activityId}/aliases");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        await response.EnsureSuccessOrThrowAsync("check activity entity");
        return true;
    }

    private static async Task<bool> ActivityAliasExistsAsync(
        HttpClient client, string qualifiedId, string alias, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{DaBasePath}/activities/{qualifiedId}+{alias}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        await response.EnsureSuccessOrThrowAsync("check activity alias");
        return true;
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
