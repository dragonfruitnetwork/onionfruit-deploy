using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Serilog;
using FileMode = System.IO.FileMode;

namespace DragonFruit.OnionFruit.Deploy.Distribution;

public class MacOSVelopackBuildDistributor(
    string applicationName,
    string operatingSystemName,
    string runtimeIdentifier,
    string channel,
    string? extraArgs,
    string? appBundlePath,
    Architecture architecture)
    : VelopackBuildDistributor(applicationName, operatingSystemName, runtimeIdentifier, channel, extraArgs, appBundlePath)
{
    private readonly string _dmgPath = Path.Combine(Program.ReleasesDirectory, $"OnionFruit ({architecture.ToString().ToLowerInvariant()}).dmg");

    protected override async Task PostPackageAction()
    {
        if (Program.MacOSConfig["CreateInstallDMG"]?.ToLowerInvariant() is not "true" and not "yes")
        {
            // production builds should create a DMG, so if this is skipped, assume a test build and don't do any notarization
            return;
        }
        
        // remove the old app bundle
        Directory.Delete(appBundlePath, true);
        
        // extract the .app bundle from the portable .zip file
        var portableFile = Directory.EnumerateFiles(Program.ReleasesDirectory, "*-Portable.zip", SearchOption.TopDirectoryOnly).SingleOrDefault();
        if (portableFile == null)
        {
            Log.Warning("Portable .zip file not found. Skipping DMG creation.");
            return;
        }

        // don't use built in zip extraction as it seems to break code signing
        await Program.RunCommand("ditto", $"-xk \"{portableFile}\" \"{Program.StagingDirectory}\"");
        
        var bundleName = $"{PackTitle}.app";
        var iconsDirectory = Program.MacOSConfig["IconsDirectory"];
        var signingIdentity = Program.MacOSConfig["CodeSigningIdentity"];
        var notaryProfileKeychain = string.IsNullOrEmpty(signingIdentity) ? null : Program.MacOSConfig["NotaryKeychainProfile"];
        
        var icnsFile = Directory.Exists(iconsDirectory) ? Directory.EnumerateFiles(iconsDirectory, "*.icns", SearchOption.TopDirectoryOnly).SingleOrDefault() : null;

        await Program.RunCommand("create-dmg", $"--volname \"Install OnionFruit\""
                                         + $" --filesystem APFS"
                                         + $" --window-pos 200 120"
                                         + $" --window-size 800 400"
                                         + $" --app-drop-link 600 185"
                                         + $" --no-internet-enable"
                                         + $" --icon \"{bundleName}\" 200 200"
                                         + $" --icon-size 100"
                                         + $" --hide-extension \"{bundleName}\""
                                         + (icnsFile != null ? $" --volicon \"{icnsFile}\"" : string.Empty)
                                         + (string.IsNullOrWhiteSpace(signingIdentity) ? string.Empty : $" --codesign \"{signingIdentity}\"")
#if !DEBUG
                                         + (string.IsNullOrWhiteSpace(notaryProfileKeychain) ? string.Empty : $" --notarize \"{notaryProfileKeychain}\"")
#endif
                                         + $" \"{_dmgPath}\" \"{Path.Combine(Program.StagingDirectory, bundleName)}\"");

        // remove portable .zip, replace with the .dmg and update the assets.*.json file
        var assetsJsonFile = Directory.EnumerateFiles(Program.ReleasesDirectory, "assets.*.json", SearchOption.TopDirectoryOnly).SingleOrDefault();
        var legacyReleasesFile = Directory.EnumerateFiles(Program.ReleasesDirectory, "RELEASES-*", SearchOption.TopDirectoryOnly).SingleOrDefault();

        if (legacyReleasesFile != null)
        {
            Log.Information("Removing legacy releases file: {LegacyReleasesFile}", Path.GetFileName(legacyReleasesFile));
            File.Delete(legacyReleasesFile);
        }
        
        if (assetsJsonFile == null)
        {
            Log.Warning("Portable .zip file or assets.json file not found. Skipping removal and update.");
            return;
        }
        
        Log.Information("Removing portable .zip file: {PortableFile}", portableFile);
        File.Delete(portableFile);

        await using var jsonStream = new FileStream(assetsJsonFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        var assetsArray = await JsonSerializer.DeserializeAsync<JsonArray>(jsonStream);

        foreach (var item in assetsArray?.Where(x => x != null) ?? [])
        {
            if (item!["RelativeFileName"]?.GetValue<string>() != Path.GetFileName(portableFile))
            {
                continue;
            }

            item["RelativeFileName"] = Path.GetFileName(_dmgPath);
            break;
        }

        jsonStream.SetLength(0); // truncate in case the new content is smaller
        await JsonSerializer.SerializeAsync(jsonStream, assetsArray);
    }
}