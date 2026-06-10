using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class InputConfig
{
    [YamlMember(Alias = "model")]
    public CloudModelInput Model { get; set; } = new();

    [YamlMember(Alias = "tool")]
    public ToolConfig? Tool { get; set; }
}
