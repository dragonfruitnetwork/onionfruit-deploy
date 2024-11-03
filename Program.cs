using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DragonFruit.OnionFruit.Deploy.Build;
using Microsoft.Extensions.Configuration;
using Octokit;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace DragonFruit.OnionFruit.Deploy;

public static class Program
{
    private static readonly IConfiguration Config;
    
    public static string ReleasesDirectory { get; } = Path.Combine(Environment.CurrentDirectory, "releases");
    public static string StagingDirectory { get; } = Path.Combine(Environment.CurrentDirectory, "staging");

    internal static string SolutionName => Config["SolutionName"] ?? throw new InvalidOperationException("SolutionName not set in app.config");

    internal static string GitHubRepoUser => Config["GitHub:User"] ?? string.Empty;
    internal static string GitHubRepoName => Config["GitHub:Repo"] ?? string.Empty;
    internal static string GitHubAccessToken => Config["GitHub:Token"] ?? string.Empty;
    internal static string GitHubRepoUrl => CanUseGitHub ? $"https://github.com/{GitHubRepoUser}/{GitHubRepoName}" : string.Empty;

    internal static bool CanUseGitHub => !string.IsNullOrEmpty(GitHubAccessToken) && !string.IsNullOrEmpty(GitHubRepoName) && !string.IsNullOrEmpty(GitHubRepoUser);

    internal static string VelopackId => Config["Velopack:PackageId"] ?? string.Empty;
    internal static string VelopackIcon => Config["Velopack:PackageIcon"] ?? string.Empty;
    internal static string VelopackIconPath => Path.GetFullPath(Path.Combine(SolutionPath, VelopackIcon));

    internal static string CodeSignCert => Config["CodeSign:Certificate"] ?? string.Empty;
    internal static string CodeSignCertPassword => Config["CodeSign:Password"] ?? string.Empty;

    public static GitHubClient? GitHubClient { get; private set; }

    internal static string ProjectLocation { get; private set; } = null!;
    internal static string SolutionPath { get; private set; } = null!;

    static Program()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.With(new ProcessAgeEnricher())
            .WriteTo.Console(outputTemplate: "> [{ProcessAge} {Level}]: {Message}{NewLine}", theme: AnsiConsoleTheme.Literate)
            .CreateLogger();

        Config = new ConfigurationBuilder()
            .AddXmlFile(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "oniondeploy.xml"), optional: true)
            .AddEnvironmentVariables("ONIONDEPLOY_")
            .Build();
    }
    
    public static async Task Main(string[] args)
    {
        if (args.Length < 2)
        {
            Log.Information("Usage: [csproj file location] [runtime identifier] [version]");
            return;
        }
        
        if (CanUseGitHub)
        {
            GitHubClient = new GitHubClient(new ProductHeaderValue("OnionFruit-Deploy"))
            {
                Credentials = new Credentials(GitHubAccessToken)
            };
        }
        
        ProjectLocation = GetArg(0) ?? string.Empty;

        if (Path.GetExtension(ProjectLocation) != ".csproj" || !File.Exists(ProjectLocation))
        {
            Log.Error("Invalid project file");
            return;
        }

        var fullProjectDir = Path.IsPathRooted(ProjectLocation) ? ProjectLocation : Path.Combine(Environment.CurrentDirectory, ProjectLocation);
        FindSolutionPath(Path.GetDirectoryName(fullProjectDir)!);

        var version = GetArg(2) ?? await GetVersionFromPublicReleasesAsync();
        Log.Information("Building version {version}", version);

        ProgramBuilder builder;
        
        switch (GetArg(1))
        {
            case "win-x64":
                builder = new WindowsProgramBuilder(version, Architecture.X64);
                break;

            case "win-arm64":
                builder = new WindowsProgramBuilder(version, Architecture.Arm64);
                break;

            default:
                Log.Error("Unsupported platform {platform}", GetArg(1));
                return;
        }
        
        var distributor = builder.CreateBuildDistributor();

        Log.Information("Performing build...");
        await builder.BuildAsync();
        
        Log.Information("Restoring build...");
        await distributor.RestoreBuild();

        Log.Information("Pack n' Publishing build...");
        await distributor.PublishBuild(version);
        
        if (CanUseGitHub)
        {
            Process.Start(new ProcessStartInfo($"{GitHubRepoUrl}/releases")
            {
                UseShellExecute = true,
                Verb = "open"
            });
        }
        
        Log.Information("Build complete");
    }
    
    public static async Task<bool> RunCommand(string command, string args, bool useSolutionPath = true, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = useSolutionPath ? SolutionPath : Environment.CurrentDirectory
        };
        
        using var process = Process.Start(psi);
        
        Debug.Assert(process != null);

        process.ErrorDataReceived += (_, err) => Log.Error(err.Data!);
        process.OutputDataReceived += (_, output) => Log.Debug(output.Data!);

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Log.Error("Command {command:l} failed with exit code {exitCode}", $"{process.StartInfo.FileName} {process.StartInfo.Arguments}", process.ExitCode);
            
            if (throwOnError)
            {
                throw new InvalidOperationException($"Command {command} failed with exit code {process.ExitCode}");
            }

            return false;
        }

        return true;
    }
    
    private static string? GetArg(int index)
    {
        var args = Environment.GetCommandLineArgs();
        return args.Length > ++index ? args[index] : null;
    }
    
    private static void FindSolutionPath(string path)
    {
        while (true)
        {
            if (File.Exists(Path.Combine(path, SolutionName)))
                break;

            path = Path.GetFullPath(Path.Combine(path, ".."));
        }

        SolutionPath = path;
    }

    private static async Task<string> GetVersionFromPublicReleasesAsync()
    {
        Release? latestRelease = null;
        
        if (CanUseGitHub)
        {
            var latestReleases = await GitHubClient!.Repository.Release.GetAll(GitHubRepoUser, GitHubRepoName, new ApiOptions
            {
                PageSize = 1
            });
            
            latestRelease = latestReleases.SingleOrDefault();
        }
        
        // get latest release for incrementing
        var version = DateTime.Now.ToString("yyyy.Mdd.");

        if (latestRelease?.Draft == false && latestRelease.TagName.StartsWith(version, StringComparison.InvariantCulture))
        {
            version += int.Parse(latestRelease.TagName.Split('.')[2]) + 1;
        }
        else
        {
            version += "0";
        }
        
        return version;
    }
}