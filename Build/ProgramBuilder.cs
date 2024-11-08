using System.IO;
using System.Threading.Tasks;
using DragonFruit.OnionFruit.Deploy.Distribution;

namespace DragonFruit.OnionFruit.Deploy.Build;

public abstract class ProgramBuilder
{
    protected readonly string Version;

    protected ProgramBuilder(string version)
    {
        Version = version;

        if (Directory.Exists(Program.StagingDirectory))
        {
            Directory.Delete(Program.StagingDirectory, true);
        }

        Directory.CreateDirectory(Program.StagingDirectory);
    }

    protected abstract string RuntimeIdentifier { get; }
    public abstract string ExecutableName { get; }

    public abstract IBuildDistributor CreateBuildDistributor();

    public virtual Task BuildAsync() => RunDotnetPublish();

    protected Task RunDotnetPublish(string? extraArgs = null, string? outputDir = null)
    {
        return Program.RunCommand("dotnet", $"publish"
                                            + $" -r {RuntimeIdentifier}"
                                            + $" -c Release"
                                            + $" -o \"{outputDir ?? Program.StagingDirectory}\""
                                            + $" -p:Version={Version}"
                                            + $" --self-contained"
                                            + $" {extraArgs ?? string.Empty}"
                                            + $" {Program.ProjectLocation}");
    }
}