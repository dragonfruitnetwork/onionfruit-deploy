using System;
using System.Linq;
using System.Threading.Tasks;
using Octokit;

namespace DragonFruit.OnionFruit.Deploy.Distribution;

public class WindowsVelopackBuildDistributor(string applicationName, string operatingSystemName, string runtimeIdentifier, string channel, string? extraArgs = null, string? stagingPath = null)
    : VelopackBuildDistributor(applicationName, operatingSystemName, runtimeIdentifier, channel, extraArgs, stagingPath)
{
    public override async Task PublishBuild(string version)
    {
        await base.PublishBuild(version);

        var installerSuffix = channel switch
        {
            "win-arm64" => "arm64",
            "win" => "x64",

            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
        };

        if (Program.GitHubClient != null)
        {
            var releaseFiles = await Program.GitHubClient.Repository.Release.GetLatest(Program.GitHubRepoUser, Program.GitHubRepoName);

            var installerAsset = releaseFiles.Assets.Single(x => x.Name.Equals($"{Program.VelopackId}-{channel}-Setup.exe"));
            var releasesAsset = releaseFiles.Assets.Single(x => x.Name.Equals("RELEASES"));

            await Program.GitHubClient.Repository.Release.EditAsset(Program.GitHubRepoUser, Program.GitHubRepoName, installerAsset.Id, new ReleaseAssetUpdate($"install-{installerSuffix}.exe"));
            await Program.GitHubClient.Repository.Release.EditAsset(Program.GitHubRepoUser, Program.GitHubRepoName, releasesAsset.Id, new ReleaseAssetUpdate("ONIONFRUITUPGRADE"));
        }
    }
}