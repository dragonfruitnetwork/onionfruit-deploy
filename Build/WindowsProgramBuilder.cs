using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DragonFruit.OnionFruit.Deploy.Distribution;
using Serilog;

namespace DragonFruit.OnionFruit.Deploy.Build;

public class WindowsProgramBuilder(string version, Architecture arch) : ProgramBuilder(version)
{
    private const string OSName = "win";

    protected override string RuntimeIdentifier => arch switch
    {
        Architecture.X64 => $"{OSName}-x64",
        Architecture.Arm64 => $"{OSName}-arm64",

        _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, null)
    };

    public override string ExecutableName => "DragonFruit.OnionFruit.Windows.exe";

    public override IBuildDistributor CreateBuildDistributor()
    {
        var packageIconPath = Program.WindowsConfig["PackageIcon"];
        var signToolPath = Program.WindowsConfig["SigntoolPath"] ?? "signtool.exe";
        var signSection = Program.WindowsConfig.GetSection("Certificates");

        var extraArgs = new StringBuilder("--noPortable");

        if (File.Exists(packageIconPath))
        {
            extraArgs.Append($" --icon=\"{packageIconPath}\"");
        }
        else
        {
            Log.Warning("Package icon '{PackageIconPath}' does not exist, skipping icon embedding", packageIconPath);
        }

        // collect signing certificates from config (supports env var and xml formats)
        var certificates = new List<(string FilePath, string? Password)>();

        foreach (var section in signSection.GetChildren())
        {
            var certPath = section.Value ?? section["File"];
            var certPassword = section["Password"] ?? section["Pass"];

            if (string.IsNullOrEmpty(certPath))
            {
                Log.Warning("Certificate section '{SectionKey}' has no file path configured, skipping", section.Key);
                continue;
            }

            if (!File.Exists(certPath))
            {
                Log.Warning("Code signing certificate '{CertPath}' (from '{SectionKey}') does not exist, skipping", certPath, section.Key);
                continue;
            }

            certificates.Add((certPath, certPassword));
        }

        if (certificates.Count > 0)
        {
            var signTemplate = new StringBuilder();
            for (var i = 0; i < certificates.Count; i++)
            {
                var (certPath, certPassword) = certificates[i];

                if (i > 0)
                {
                    signTemplate.Append(" && ");
                }

                signTemplate.Append($"\\\"{signToolPath}\\\" sign");

                if (i > 0)
                {
                    signTemplate.Append(" /as");
                }

                signTemplate.Append($" /td sha256 /fd sha256 /f \\\"{certPath}\\\" /tr http://timestamp.acs.microsoft.com");

                if (!string.IsNullOrEmpty(certPassword))
                {
                    signTemplate.Append($" /p \\\"{certPassword}\\\"");
                }

                signTemplate.Append(" {{file}}");
            }

            extraArgs.Append($" --signTemplate=\"{signTemplate}\"");
        }
        else
        {
            Log.Warning("No valid code signing certificates found, skipping code signing");
        }
        
        var channelName = arch switch
        {
            Architecture.X64 => OSName,
            Architecture.Arm64 => $"{OSName}-arm64",
            
            _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, null)
        };

        return new WindowsVelopackBuildDistributor(ExecutableName, OSName, RuntimeIdentifier, channelName, extraArgs: extraArgs.ToString());
    }
}