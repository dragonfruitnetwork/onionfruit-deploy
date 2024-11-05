using System.Threading.Tasks;
using Serilog;

namespace DragonFruit.OnionFruit.Deploy.Distribution;

public class VelopackBuildDistributor : IBuildDistributor
{
    private readonly string _applicationName;
    private readonly string _operatingSystemName;
    private readonly string _runtimeIdentifier;
    private readonly string _channel;
    private readonly string? _extraArgs;
    private readonly string _stagingPath;

    public VelopackBuildDistributor(string applicationName, string operatingSystemName, string runtimeIdentifier, string channel, string? extraArgs = null, string? stagingPath = null)
    {
        _applicationName = applicationName;
        _operatingSystemName = operatingSystemName;
        _runtimeIdentifier = runtimeIdentifier;
        _channel = channel;
        _extraArgs = extraArgs;
        _stagingPath = stagingPath ?? Program.StagingDirectory;
    }

    protected virtual string PackTitle => "OnionFruit\u2122";

    public virtual async Task RestoreBuild()
    {
        if (!Program.CanUseGitHub)
        {
            return;
        }

        await Program.RunCommand("dotnet", $"tool run vpk download github"
                                        + $" --pre"
                                        + $" --repoUrl=\"{Program.GitHubRepoUrl}\""
                                        + $" --token=\"{Program.GitHubAccessToken}\""
                                        + $" --channel=\"{_channel}\""
                                        + $" --outputDir=\"{Program.ReleasesDirectory}\"",
            throwOnError: false);
    }


    public virtual async Task PublishBuild(string version)
    {
        await Program.RunCommand("dotnet", $"tool run vpk [{_operatingSystemName}] pack"
                                        + $" --packTitle=\"{PackTitle}\""
                                        + $" --packAuthors=\"DragonFruit Network\""
                                        + $" --packId=\"{Program.VelopackId}\""
                                        + $" --packVersion=\"{version}\""
                                        + $" --outputDir=\"{Program.ReleasesDirectory}\""
                                        + $" --mainExe=\"{_applicationName}\""
                                        + $" --packDir=\"{_stagingPath}\""
                                        + $" --channel=\"{_channel}\""
                                        + $" --runtime=\"{_runtimeIdentifier}\""
                                        + " --verbose"
                                        + $" {_extraArgs}");

        if (Program.CanUseGitHub)
        {
            Log.Information("Uploading release {version:l}-{channel:l} to GitHub", version, _channel);
            await Program.RunCommand("dotnet", $"tool run vpk upload github"
                                            + $" --repoUrl=\"{Program.GitHubRepoUrl}\""
                                            + $" --token=\"{Program.GitHubAccessToken}\""
                                            + $" --outputDir=\"{Program.ReleasesDirectory}\""
                                            + $" --tag=\"{version}\""
                                            + $" --releaseName=\"{version}\""
                                            + $" --merge"
                                            + $" --channel=\"{_channel}\"");
        }
    }
}