using System.Text.Json.Serialization;

namespace RevitCli.Models.Api;

public class WorkItemResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("reportUrl")]
    public string? ReportUrl { get; set; }

    [JsonPropertyName("stats")]
    public WorkItemStats? Stats { get; set; }
}

public class WorkItemStats
{
    [JsonPropertyName("timeQueued")]
    public string? TimeQueued { get; set; }

    [JsonPropertyName("timeDownloadStarted")]
    public string? TimeDownloadStarted { get; set; }

    [JsonPropertyName("timeInstructionsStarted")]
    public string? TimeInstructionsStarted { get; set; }

    [JsonPropertyName("timeInstructionsEnded")]
    public string? TimeInstructionsEnded { get; set; }

    [JsonPropertyName("timeUploadEnded")]
    public string? TimeUploadEnded { get; set; }
}
