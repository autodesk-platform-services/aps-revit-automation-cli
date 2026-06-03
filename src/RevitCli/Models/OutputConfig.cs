using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class OutputConfig
{
    [YamlMember(Alias = "result")]
    public OutputEntry Result { get; set; } = new();
}
