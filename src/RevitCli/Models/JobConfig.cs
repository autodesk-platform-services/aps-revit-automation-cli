using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class JobConfig
{
    [YamlMember(Alias = "authentication")]
    public AuthenticationConfig Authentication { get; set; } = new();

    [YamlMember(Alias = "revit")]
    public RevitConfig Revit { get; set; } = new();

    [YamlMember(Alias = "app")]
    public AppConfig App { get; set; } = new();

    [YamlMember(Alias = "inputs")]
    public InputConfig Inputs { get; set; } = new();

    [YamlMember(Alias = "outputs")]
    public OutputConfig Outputs { get; set; } = new();
}
