﻿using System;
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

    internal static string SolutionName => Config["SolutionName"] ?? throw new InvalidOperationException("SolutionName not set in oniondeploy.xml");

    internal static string GitHubRepoUser => Config["GitHub:User"] ?? string.Empty;
    internal static string GitHubRepoName => Config["GitHub:Repo"] ?? string.Empty;
    internal static string GitHubAccessToken => Config["GitHub:Token"] ?? string.Empty;
    internal static string GitHubRepoUrl => CanUseGitHub ? $"https://github.com/{GitHubRepoUser}/{GitHubRepoName}" : string.Empty;

    internal static bool CanUseGitHub => !string.IsNullOrEmpty(GitHubAccessToken) && !string.IsNullOrEmpty(GitHubRepoName) && !string.IsNullOrEmpty(GitHubRepoUser);

    internal static string VelopackId => Config["Velopack:PackageId"] ?? string.Empty;
    
    internal static IConfiguration MacOSConfig => Config.GetSection("MacOS");
    internal static IConfiguration WindowsConfig => Config.GetSection("Windows");

    public static GitHubClient? GitHubClient { get; private set; }

    internal static string ProjectLocation { get; private set; } = null!;

    static Program()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.With(new ProcessAgeEnricher())
            .WriteTo.Console(outputTemplate: "> [{ProcessAge} {Level}]: {Message}{NewLine}", theme: AnsiConsoleTheme.Literate)
            .CreateLogger();
        
        Config = new ConfigurationBuilder()
            .AddXmlFile(Path.Combine(Environment.CurrentDirectory, "oniondeploy.xml"), optional: true)
            .AddXmlFile(Path.Combine(Environment.CurrentDirectory, "oniondeploy.local.xml"), optional: true)
            .AddEnvironmentVariables("ONIONDEPLOY_")
            .Build();
    }
    
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Log.Information("Usage: [csproj file location] [runtime identifier] [version]");
            return -1;
        }
        
        ProjectLocation = GetArg(0) ?? string.Empty;

        if (Path.GetExtension(ProjectLocation) != ".csproj" || !File.Exists(ProjectLocation))
        {
            Log.Error("Invalid project file");
            return -1;
        }

        // reset the cwd to the solution directory
        Environment.CurrentDirectory = FindSolutionPath(Path.GetDirectoryName(Path.IsPathRooted(ProjectLocation) ? ProjectLocation : Path.Combine(Environment.CurrentDirectory, ProjectLocation))!);
        
        if (CanUseGitHub)
        {
            GitHubClient = new GitHubClient(new ProductHeaderValue("OnionFruit-Deploy"))
            {
                Credentials = new Credentials(GitHubAccessToken)
            };
        }
        
        var version = GetArg(2) ?? await GetVersionFromPublicReleasesAsync();
        Log.Information("OnionFruit Deploy v{version:l} building {appVersion:l}", Assembly.GetExecutingAssembly().GetName().Version!.ToString(3), version);

        ProgramBuilder builder;
        
        switch (GetArg(1))
        {
            case "win-x64":
                builder = new WindowsProgramBuilder(version, Architecture.X64);
                break;

            case "win-arm64":
                builder = new WindowsProgramBuilder(version, Architecture.Arm64);
                break;
            
            case "osx-x64" when OperatingSystem.IsMacOS():
                builder = new MacOSProgramBuilder(version, Architecture.X64);
                break;
            
            case "osx-arm64" when OperatingSystem.IsMacOS():
                builder = new MacOSProgramBuilder(version, Architecture.Arm64);
                break;

            default:
                Log.Error("Unsupported platform {platform}", GetArg(1));
                return -1;
        }
        
        var distributor = builder.CreateBuildDistributor();

        if (Config["SkipBuild"]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!File.Exists(Path.Combine(StagingDirectory, builder.ExecutableName)))
            {
                Log.Error("Build was skipped but no executable was found in the staging directory");
                return -1;
            }
            
            Log.Information("Build skipped, restoring and publishing only...");
        }
        else
        {
            Log.Information("Performing build...");
            await builder.BuildAsync();
        }
        
        Log.Information("Restoring build...");
        await distributor.RestoreBuild();

        Log.Information("Pack 'n' Publishing build...");
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
        return 0;
    }
    
    public static async Task<bool> RunCommand(string command, string args, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Environment.CurrentDirectory
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
    
    private static string FindSolutionPath(string path)
    {
        while (true)
        {
            if (File.Exists(Path.Combine(path, SolutionName)))
                break;

            path = Path.GetFullPath(Path.Combine(path, ".."));
        }

        return path;
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