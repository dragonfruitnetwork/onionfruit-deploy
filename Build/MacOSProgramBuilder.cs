using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DragonFruit.OnionFruit.Deploy.Distribution;
using Microsoft.VisualBasic.FileIO;
using Serilog;

namespace DragonFruit.OnionFruit.Deploy.Build;

public class MacOSProgramBuilder(string version, Architecture arch) : ProgramBuilder(version)
{
    private const string OSName = "osx";
    private const string UpdaterBranchName = "mac";
    private const string AppBundleName = "OnionFruit.app";

    protected override string RuntimeIdentifier => arch switch
    {
        Architecture.X64 => $"{OSName}-x64",
        Architecture.Arm64 => $"{OSName}-arm64",

        _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, null)
    };

    public override string ExecutableName => "onionfruit";

    public override async Task BuildAsync()
    {
        // create the app bundle and directories
        var bundleRoot = Path.Combine(Program.StagingDirectory, AppBundleName + ".tmp");

        var executablesDirectory = CreateAndReturnDirectory(Path.Combine(bundleRoot, "Contents", "MacOS"));
        var appResourcesDirectory = CreateAndReturnDirectory(Path.Combine(bundleRoot, "Contents", "Resources"));
        var launchDaemonsDirectory = CreateAndReturnDirectory(Path.Combine(bundleRoot, "Contents", "Library", "LaunchDaemons"));

        Log.Information("Building into app bundle at '{BundleRoot}'", bundleRoot);
        await RunDotnetPublish(outputDir: executablesDirectory);
        
        var infoPlistPath = Program.MacOSConfig["InfoPlist"];
        var launchDaemonPlistPath = Program.MacOSConfig["ServicePlist"];

        if (string.IsNullOrWhiteSpace(infoPlistPath) || !File.Exists(infoPlistPath))
        {
            throw new FileNotFoundException("Info.plist file not provided or not found.", infoPlistPath);
        }

        if (string.IsNullOrWhiteSpace(launchDaemonPlistPath) || !File.Exists(launchDaemonPlistPath))
        {
            throw new FileNotFoundException("Service plist file not provided or not found.", launchDaemonPlistPath);
        }
        
        // copy launch daemon config plist
        // todo read the launchdaemon plist and check the app is where it says it is
        Log.Information("Copying launch daemon plist to '{LaunchDaemonsDirectory}'", launchDaemonsDirectory);
        File.Copy(launchDaemonPlistPath, Path.Combine(launchDaemonsDirectory, Path.GetFileName(launchDaemonPlistPath)));

        // copy icons to resources
        var iconsDirectory = Program.MacOSConfig["IconsDirectory"];
        if (!string.IsNullOrWhiteSpace(iconsDirectory) && Directory.Exists(iconsDirectory))
        {
            Log.Information("Copying icons from '{IconsDirectory}' to '{AppResourcesDirectory}'", iconsDirectory, appResourcesDirectory);
            FileSystem.CopyDirectory(iconsDirectory, appResourcesDirectory);
        }
        else
        {
            Log.Warning("Icons directory '{IconsDirectory}' does not exist or is not specified, skipping icon copy.", iconsDirectory);
        }

        // write Info.plist with correct version and copyright year
        var bundleInfoPlist = Path.Combine(bundleRoot, "Contents", "Info.plist");
        
        File.Copy(infoPlistPath, bundleInfoPlist);

        await Program.RunCommand("/usr/libexec/PlistBuddy", ""
            + $" -c \"Set :NSHumanReadableCopyright Copyright {DateTime.UtcNow.Year} © DragonFruit Network\""
            + $" -c \"Set :CFBundleShortVersionString {Version}\""
            + $" \"{bundleInfoPlist}\"");
        
        // mark it as the final bundle
        Directory.Move(bundleRoot, Path.Combine(Program.StagingDirectory, AppBundleName));
    }

    public override IBuildDistributor CreateBuildDistributor()
    {
        var extraArgs = new StringBuilder($"--noInst --signAppIdentity=\"{Program.MacOSConfig["CodeSigningIdentity"] ?? "-"}\"");
        var entitlementsPlist = Program.MacOSConfig["EntitlementsPlist"];

        if (File.Exists(entitlementsPlist))
        {
            extraArgs.Append($" --signEntitlements=\"{entitlementsPlist}\"");
        }

        var channelName = arch switch
        {
            Architecture.X64 => $"{UpdaterBranchName}-x64",
            Architecture.Arm64 => $"{UpdaterBranchName}-arm64",
            
            _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, null)
        };

        return new MacOSVelopackBuildDistributor(ExecutableName, OSName, RuntimeIdentifier, channelName, extraArgs.ToString(), Path.Combine(Program.StagingDirectory, AppBundleName));
    }
    
    private static string CreateAndReturnDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        Directory.CreateDirectory(path);
        return path;
    }
}