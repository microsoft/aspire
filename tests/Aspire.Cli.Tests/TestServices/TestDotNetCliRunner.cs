// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.DotNet;
using Aspire.Cli.Utils;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDotNetCliRunner : IDotNetCliRunner
{
    public Func<FileInfo, string, string, string?, bool, ProcessInvocationOptions, CancellationToken, int>? AddPackageAsyncCallback { get; set; }
    public Func<FileInfo, FileInfo, ProcessInvocationOptions, CancellationToken, int>? AddProjectToSolutionAsyncCallback { get; set; }
    public Func<FileInfo, bool, ProcessInvocationOptions, CancellationToken, int>? BuildAsyncCallback { get; set; }
    public Func<FileInfo, ProcessInvocationOptions, CancellationToken, int>? RestoreAsyncCallback { get; set; }
    public Func<FileInfo, ProcessInvocationOptions, CancellationToken, (int ExitCode, bool IsAspireHost, string? AspireHostingVersion)>? GetAppHostInformationAsyncCallback { get; set; }
    public Func<DirectoryInfo, ProcessInvocationOptions, CancellationToken, (int ExitCode, string[] ConfigPaths)>? GetNuGetConfigPathsAsyncCallback { get; set; }
    public Func<FileInfo, string[], string[], ProcessInvocationOptions, CancellationToken, (int ExitCode, JsonDocument? Output)>? GetProjectItemsAndPropertiesAsyncCallback { get; set; }
    public Func<string, string, string?, bool, ProcessInvocationOptions, CancellationToken, (int ExitCode, string? TemplateVersion)>? InstallTemplateAsyncCallback { get; set; }
    public Func<string, string, string, ProcessInvocationOptions, CancellationToken, int>? NewProjectAsyncCallback { get; set; }
    public Func<FileInfo, bool, bool, bool, string[], IDictionary<string, string>?, TaskCompletionSource<IAppHostCliBackchannel>?, ProcessInvocationOptions, CancellationToken, Task<int>>? RunAsyncCallback { get; set; }
    public Func<DirectoryInfo, string, bool, bool, int, int, FileInfo?, bool, ProcessInvocationOptions, CancellationToken, (int ExitCode, NuGetPackage[]? Packages)>? SearchPackagesAsyncCallback { get; set; }
    public Func<FileInfo, ProcessInvocationOptions, CancellationToken, (int ExitCode, IReadOnlyList<FileInfo> Projects)>? GetSolutionProjectsAsyncCallback { get; set; }
    public Func<FileInfo, FileInfo, ProcessInvocationOptions, CancellationToken, int>? AddProjectReferenceAsyncCallback { get; set; }

    public Task<int> AddPackageAsync(FileInfo projectFilePath, string packageName, string packageVersion, string? nugetSource, bool noRestore, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return AddPackageAsyncCallback != null
            ? Task.FromResult(AddPackageAsyncCallback(projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken))
            : throw new NotImplementedException();
    }

    public Task<int> AddProjectToSolutionAsync(FileInfo solutionFile, FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return AddProjectToSolutionAsyncCallback != null
            ? Task.FromResult(AddProjectToSolutionAsyncCallback(solutionFile, projectFile, options, cancellationToken))
            : Task.FromResult(0); // If not overridden, just return success.
    }

    public Task<int> BuildAsync(FileInfo projectFilePath, bool noRestore, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return BuildAsyncCallback != null
            ? Task.FromResult(BuildAsyncCallback(projectFilePath, noRestore, options, cancellationToken))
            : throw new NotImplementedException();
    }

    public Task<int> RestoreAsync(FileInfo projectFilePath, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return RestoreAsyncCallback != null
            ? Task.FromResult(RestoreAsyncCallback(projectFilePath, options, cancellationToken))
            : throw new NotImplementedException();
    }

    public Task<(int ExitCode, bool IsAspireHost, string? AspireHostingVersion)> GetAppHostInformationAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        var informationalVersion = VersionHelper.GetDefaultTemplateVersion();

        return GetAppHostInformationAsyncCallback != null
            ? Task.FromResult(GetAppHostInformationAsyncCallback(projectFile, options, cancellationToken))
            : Task.FromResult<(int, bool, string?)>((0, true, informationalVersion));
    }

    public Task<(int ExitCode, string[] ConfigPaths)> GetNuGetConfigPathsAsync(DirectoryInfo workingDirectory, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return GetNuGetConfigPathsAsyncCallback != null
            ? Task.FromResult(GetNuGetConfigPathsAsyncCallback(workingDirectory, options, cancellationToken))
            : Task.FromResult((0, GetGlobalNuGetPaths())); // If not overridden, return success with no config paths which will blow up.
    }

    private static string[] GetGlobalNuGetPaths()
    {
        return Environment.OSVersion.Platform switch
        {
            PlatformID.Win32NT => [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config")],
            _ => [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet.Config")],
        };
    }

    public Task<(int ExitCode, JsonDocument? Output)> GetProjectItemsAndPropertiesAsync(FileInfo projectFile, string[] items, string[] properties, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return GetProjectItemsAndPropertiesAsyncCallback != null
            ? Task.FromResult(GetProjectItemsAndPropertiesAsyncCallback(projectFile, items, properties, options, cancellationToken))
            : throw new NotImplementedException();
    }

    public Task<(int ExitCode, string? TemplateVersion)> InstallTemplateAsync(string packageName, string version, FileInfo? nugetConfigFile, string? nugetSource, bool force, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return InstallTemplateAsyncCallback != null
            ? Task.FromResult(InstallTemplateAsyncCallback(packageName, version, nugetSource, force, options, cancellationToken))
            : Task.FromResult<(int, string?)>((0, version)); // If not overridden, just return success for the version specified.
    }

    public Task<int> NewProjectAsync(string templateName, string name, string outputPath, string[] extraArgs, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return NewProjectAsyncCallback != null
            ? Task.FromResult(NewProjectAsyncCallback(templateName, name, outputPath, options, cancellationToken))
            : Task.FromResult(0); // If not overridden, just return success.
    }

    public Task<int> RunAsync(FileInfo projectFile, bool watch, bool noBuild, bool noRestore, string[] args, IDictionary<string, string>? env, TaskCompletionSource<IAppHostCliBackchannel>? backchannelCompletionSource, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return RunAsyncCallback != null
            ? RunAsyncCallback(projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken)
            : throw new NotImplementedException();
    }

    public Task<(int ExitCode, NuGetPackage[]? Packages)> SearchPackagesAsync(DirectoryInfo workingDirectory, string query, bool exactMatch, bool prerelease, int take, int skip, FileInfo? nugetConfigFile, bool useCache, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return SearchPackagesAsyncCallback != null
            ? Task.FromResult(SearchPackagesAsyncCallback(workingDirectory, query, exactMatch, prerelease, take, skip, nugetConfigFile, useCache, options, cancellationToken))
            : throw new NotImplementedException();
    }

    public Task<(int ExitCode, IReadOnlyList<FileInfo> Projects)> GetSolutionProjectsAsync(FileInfo solutionFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return GetSolutionProjectsAsyncCallback != null
            ? Task.FromResult(GetSolutionProjectsAsyncCallback(solutionFile, options, cancellationToken))
            : Task.FromResult<(int, IReadOnlyList<FileInfo>)>((0, Array.Empty<FileInfo>()));
    }

    public Task<int> AddProjectReferenceAsync(FileInfo projectFile, FileInfo referencedProject, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return AddProjectReferenceAsyncCallback != null
            ? Task.FromResult(AddProjectReferenceAsyncCallback(projectFile, referencedProject, options, cancellationToken))
            : Task.FromResult(0);
    }

    public Task<int> InitUserSecretsAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
        => Task.FromResult(0);
}
