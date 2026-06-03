using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class InputConfig
{
    [YamlMember(Alias = "model")]
    public CloudModelInput Model { get; set; } = new();

    [YamlMember(Alias = "tool")]
    public string? Tool { get; set; }

    [YamlMember(Alias = "params")]
    public Dictionary<string, object>? Params { get; set; }
}
