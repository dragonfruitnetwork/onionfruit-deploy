using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DragonFruit.OnionFruit.Deploy.Distribution;
using Microsoft.VisualBasic.FileIO;
using PListNet;
using PListNet.Nodes;
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
        var bundleRoot = Path.Combine(Program.StagingDirectory, AppBundleName + ".tmp");

        Log.Information("Building into app bundle at '{BundleRoot}'", bundleRoot);
        await RunDotnetPublish(outputDir: Path.Combine(bundleRoot, "Contents", "MacOS"));

        await ProcessLaunchdPlists(Program.MacOSConfig["launchd:LaunchDaemons"], bundleRoot, Path.Combine("Contents", "Library", "LaunchDaemons"));
        await ProcessLaunchdPlists(Program.MacOSConfig["launchd:LaunchAgents"], bundleRoot, Path.Combine("Contents", "Library", "LaunchAgents"));
        await ProcessLaunchdPlists(Program.MacOSConfig["launchd:LoginItems"], bundleRoot, Path.Combine("Contents", "Library", "LoginItems"));
        
        // copy icons to resources
        var iconsDirectory = Program.MacOSConfig["IconsDirectory"];
        if (!string.IsNullOrWhiteSpace(iconsDirectory) && Directory.Exists(iconsDirectory))
        {
            var bundleResources = Path.Combine(bundleRoot, "Contents", "Resources");
            
            Log.Information("Copying icons from '{IconsDirectory}' to '{AppResourcesDirectory}'", iconsDirectory, bundleResources);
            FileSystem.CopyDirectory(iconsDirectory, bundleResources);
        }
        else
        {
            Log.Warning("Icons directory '{IconsDirectory}' does not exist or is not specified, skipping icon copy.", iconsDirectory);
        }

        // write Info.plist with correct version and copyright year
        var infoPlistPath = Program.MacOSConfig["InfoPlist"];
        var bundleInfoPlist = Path.Combine(bundleRoot, "Contents", "Info.plist");

        if (string.IsNullOrWhiteSpace(infoPlistPath) || !File.Exists(infoPlistPath))
        {
            throw new FileNotFoundException("Info.plist file not provided or not found.", infoPlistPath);
        }
        
        File.Copy(infoPlistPath, bundleInfoPlist);

        await Program.RunCommand("/usr/libexec/PlistBuddy", ""
            + $" -c \"Set :NSHumanReadableCopyright Copyright © {DateTime.UtcNow.Year} DragonFruit Network\""
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

    /// <summary>
    /// Processes either a folder of plist files or a single plist, validating the contents and copying them to the specified destination directory.
    /// </summary>
    /// <param name="plistPath">Path to either a single .plist file or a directory containing multiple .plist files</param>
    /// <param name="bundleRoot">The root of the app bundle (should be the directory ending in .app)</param>
    /// <param name="destinationDirectory">Destination directory for the plists, relative to the <see cref="bundleRoot"/></param>
    private static async Task ProcessLaunchdPlists(string? plistPath, string bundleRoot, string destinationDirectory)
    {
        if (string.IsNullOrEmpty(plistPath))
        {
            return;
        }

        var targetDirectory = Path.Combine(bundleRoot, destinationDirectory);
        var directoryCreated = Directory.Exists(targetDirectory);
        
        // if a directory is passed, process all .plist files held inside
        foreach (var plist in File.GetAttributes(plistPath) == FileAttributes.Directory ? Directory.GetFiles(plistPath, "*.plist") : [plistPath])
        {
            await using (var daemonPlistStream = File.OpenRead(plist))
            {
                var plistContents = (DictionaryNode)PList.Load(daemonPlistStream);
                var bundleProgram = (StringNode)plistContents["BundleProgram"];

                if (!File.Exists(Path.Combine(bundleRoot, bundleProgram.Value)))
                {
                    throw new FileNotFoundException("BundleProgram specified in the launch daemon plist was not found in the app bundle.", bundleProgram.Value);
                }
            }

            if (!directoryCreated)
            {
                Directory.CreateDirectory(destinationDirectory);
                directoryCreated = true;
            }

            // ensure the file ends in .plist
            var plistName = Path.ChangeExtension(Path.GetFileName(plist), ".plist");

            Log.Information("Copying plist {name} to '{DestinationPlistPath}'", plistName, targetDirectory);
            File.Copy(plist, Path.Combine(targetDirectory, plistName));
        }
    }
}