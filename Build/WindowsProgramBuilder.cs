using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DragonFruit.OnionFruit.Deploy.Distribution;

namespace DragonFruit.OnionFruit.Deploy.Build;

public class WindowsProgramBuilder(string version, Architecture arch) : ProgramBuilder(version)
{
    private const string MainExeName = "DragonFruit.OnionFruit.Windows.exe";
    private const string OSName = "win";

    protected override string RuntimeIdentifier => arch switch
    {
        Architecture.X64 => $"{OSName}-x64",
        Architecture.Arm64 => $"{OSName}-arm64",

        _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, null)
    };

    public override IBuildDistributor CreateBuildDistributor()
    {
        var extraArgs = new StringBuilder($"--noPortable --icon=\"{Program.VelopackIconPath}\"");

        if (File.Exists(Program.CodeSignCert))
        {
            extraArgs.Append($" --signParams=\"/td sha256 /fd sha256 /f \\\"{Program.CodeSignCert}\\\" /tr http://timestamp.acs.microsoft.com");
            
            if (!string.IsNullOrEmpty(Program.CodeSignCertPassword))
            {
                extraArgs.Append($" /p \"{Program.CodeSignCertPassword}\"\"");
            }
            else
            {
                extraArgs.Append('"');
            }
        }
        
        var channelName = arch switch
        {
            Architecture.X64 => OSName,
            Architecture.Arm64 => $"{OSName}-arm64",
            
            _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, null)
        };

        return new WindowsVelopackBuildDistributor(MainExeName, OSName, RuntimeIdentifier, channelName, extraArgs: extraArgs.ToString());
    }
}