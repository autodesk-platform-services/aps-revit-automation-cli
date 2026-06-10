using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class ToolConfig
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "inputs")]
    public string? Inputs { get; set; }
}
