using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class CloudModelInput
{
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    [YamlMember(Alias = "folderUrl")]
    public string? FolderUrl { get; set; }

    [YamlMember(Alias = "modelName")]
    public string? ModelName { get; set; }
}
