using System;
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
        var codeSignCert = Program.WindowsConfig["CodeSigningCertificate"];
        var codeSignCertPassword = Program.WindowsConfig["CodeSigningPassword"];

        var extraArgs = new StringBuilder("--noPortable");

        if (File.Exists(packageIconPath))
        {
            extraArgs.Append($" --icon=\"{packageIconPath}\"");
        }
        else
        {
            Log.Warning("Package icon '{PackageIconPath}' does not exist, skipping icon embedding", packageIconPath);
        }

        if (File.Exists(codeSignCert))
        {
            extraArgs.Append($" --signParams=\"/td sha256 /fd sha256 /f \\\"{codeSignCert}\\\" /tr http://timestamp.acs.microsoft.com");
            
            if (!string.IsNullOrEmpty(codeSignCertPassword))
            {
                extraArgs.Append($" /p \"{codeSignCertPassword}\"\"");
            }
            else
            {
                extraArgs.Append('"');
            }
        }
        else
        {
            Log.Warning("Code signing certificate '{CodeSignCert}' does not exist, skipping code signing", codeSignCert);
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