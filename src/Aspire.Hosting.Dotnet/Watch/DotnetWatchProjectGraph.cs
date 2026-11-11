// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREPROJECTS001

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Watch;

internal sealed record DotnetWatchProjectNode(
    string Path,
    string SdkDirectory,
    string DotnetPath,
    IReadOnlyDictionary<string, string> Properties,
    string[] References,
    string[] Outputs);

internal sealed record DotnetWatchRoot(
    IResource Resource,
    IProjectMetadata Metadata,
    string? Reason,
    IReadOnlyDictionary<string, DotnetWatchProjectNode> Projects)
{
    internal DotnetWatchProjectNode Project => Projects[DotnetWatchProjectGraph.Normalize(Metadata.ProjectPath)];
}

internal sealed record DotnetWatchPlan(DotnetWatchRoot[][] Groups, DotnetWatchRoot[] Excluded);

internal static class DotnetWatchProjectGraph
{
    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Normalize(string path) =>
        PathNormalizer.ResolveSymlinks(Path.GetFullPath(PathNormalizer.NormalizePathForCurrentPlatform(path)));

    internal static DotnetWatchPlan Partition(DotnetWatchRoot[] roots)
    {
        var remaining = new HashSet<DotnetWatchRoot>(roots);
        var groups = new List<DotnetWatchRoot[]>();
        var excluded = new List<DotnetWatchRoot>();
        while (remaining.Count > 0)
        {
            var component = new List<DotnetWatchRoot> { remaining.First() };
            remaining.Remove(component[0]);
            for (var index = 0; index < component.Count; index++)
            {
                foreach (var candidate in remaining.ToArray())
                {
                    if (SharesOutputs(component[index], candidate))
                    {
                        component.Add(candidate);
                        remaining.Remove(candidate);
                    }
                }
            }

            var unsupported = component.FirstOrDefault(root => root.Reason is not null);
            var contexts = component.Select(root => (
                Sdk: root.Project.SdkDirectory,
                Dotnet: root.Project.DotnetPath,
                WorkingDirectory: Normalize(root.Metadata.BuildWorkingDirectory ?? Path.GetDirectoryName(root.Metadata.ProjectPath)!),
                Configuration: root.Metadata is DotnetProjectMetadata metadata ? metadata.BuildConfiguration ?? "Debug" : "Debug"))
                .Distinct()
                .ToArray();
            var duplicate = component.GroupBy(root => Normalize(root.Metadata.ProjectPath), PathComparer)
                .FirstOrDefault(group => group.Count() > 1);
            var reason = unsupported is not null
                ? $"Shares build outputs with '{unsupported.Resource.Name}': {unsupported.Reason}"
                : duplicate is not null
                    ? "The watcher reports project paths, not independently controlled resource registrations."
                    : contexts.Length > 1
                        ? "Shared build outputs require incompatible SDK/build contexts."
                        : null;
            if (reason is not null)
            {
                excluded.AddRange(component.Select(root => root with { Reason = root.Reason ?? reason }));
            }
            else
            {
                groups.Add([.. component]);
            }
        }

        return new([.. groups], [.. excluded]);
    }

    private static bool SharesOutputs(DotnetWatchRoot left, DotnetWatchRoot right)
    {
        if (left.Projects.Keys.Any(right.Projects.ContainsKey))
        {
            return true;
        }

        return left.Projects.Values.SelectMany(project => project.Outputs)
            .Any(first => right.Projects.Values.SelectMany(project => project.Outputs)
                .Any(second => ContainsPath(first, second) || ContainsPath(second, first)));
    }

    private static bool ContainsPath(string directory, string path) =>
        PathComparer.Equals(directory, path) ||
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static async Task<IReadOnlyDictionary<string, DotnetWatchProjectNode>> EvaluateGraphAsync(
        IProjectMetadata metadata,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var projects = new Dictionary<string, DotnetWatchProjectNode>(PathComparer);
        var pending = new Queue<string>();
        pending.Enqueue(Normalize(metadata.ProjectPath));
        var workingDirectory = metadata.BuildWorkingDirectory ?? Path.GetDirectoryName(metadata.ProjectPath)!;
        while (pending.TryDequeue(out var path))
        {
            if (projects.ContainsKey(path))
            {
                continue;
            }

            var project = await EvaluateAsync(path, workingDirectory, configuration, cancellationToken).ConfigureAwait(false);
            projects.Add(path, project);
            foreach (var reference in project.References)
            {
                pending.Enqueue(reference);
            }
        }

        return projects;
    }

    internal static async Task<DotnetWatchProjectNode> EvaluateAsync(
        string path, string workingDirectory, string? configuration, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("aspire-watch-query-");
        try
        {
            var resultPath = Path.Combine(directory.FullName, "project.json");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // --getProperty/--getItem with no target is evaluation-only, including file apps.
            // --no-restore prevents this graph analysis from becoming a second restore/build.
            foreach (var argument in new[]
            {
                "build", path, "--no-restore", "-nologo", "-v:q",
                "-getProperty:MSBuildToolsPath,DOTNET_HOST_PATH,MSBuildProjectDirectory,MSBuildProjectFullPath,TargetFramework,TargetFrameworkVersion,TargetFrameworks,TargetFrameworkIdentifier,OutputType,TargetPath,UseAppHost,OutputPath,OutDir,BaseIntermediateOutputPath,IntermediateOutputPath,MSBuildProjectExtensionsPath,RunCommand,RunArguments,RunWorkingDirectory,WebPublishProfileFile,StartupHookSupport,PublishAot,PublishTrimmed,Optimize,DebugSymbols,MetadataUpdaterSupport",
                "-getItem:ProjectReference", $"-getResultOutputFile:{resultPath}"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (!string.IsNullOrEmpty(configuration))
            {
                startInfo.ArgumentList.Add(DotnetProjectBuildEnvironment.CreateMsBuildPropertyArgument("Configuration", configuration));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                throw new DotnetWatchToolException($"Could not evaluate watch build isolation for '{path}'.", exception);
            }
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    // Preserve cancellation if the query exits concurrently with termination.
                }
                await ((Task)Task.WhenAll(output, error)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                cancellationToken.ThrowIfCancellationRequested();
                throw new DotnetWatchToolException($"Timed out evaluating watch build isolation for '{path}'.");
            }
            if (process.ExitCode != 0)
            {
                throw new DotnetWatchToolException(
                    $"SDK evaluation for '{path}' failed with exit code {process.ExitCode}; watch output isolation is unknown.");
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false));
            // MSBuild query output: {"Properties":{"TargetPath":"..."},"Items":{"ProjectReference":[
            // {"Identity":"../Shared/Shared.csproj","FullPath":"/repo/Shared/Shared.csproj",...}]}}.
            var properties = document.RootElement.GetProperty("Properties").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.Ordinal);
            var references = new List<string>();
            foreach (var reference in document.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray())
            {
                foreach (var name in new[] { "AdditionalProperties", "SetConfiguration", "SetPlatform", "SetTargetFramework", "Targets", "GlobalPropertiesToRemove" })
                {
                    if (reference.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } setting)
                    {
                        // The Web SDK defaults even plain references to
                        // GlobalPropertiesToRemove=";WebPublishProfileFile". With no profile,
                        // this is a no-op, not an unrepresentable custom build context.
                        // https://github.com/dotnet/sdk/blob/v10.0.403/src/WebSdk/Publish/Targets/Microsoft.NET.Sdk.Publish.targets#L195-L199
                        if (name == "GlobalPropertiesToRemove" &&
                            string.IsNullOrEmpty(properties["WebPublishProfileFile"]) &&
                            setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .All(property => property.Equals("WebPublishProfileFile", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        throw new DotnetWatchToolException(
                            $"Project reference '{reference.GetProperty("Identity").GetString()}' changes its build context through '{name}'; watch isolation is unknown.");
                    }
                }
                var referencePath = reference.GetProperty("FullPath").GetString()
                    ?? throw new DotnetWatchToolException("Project reference evaluation returned no physical path.");
                referencePath = ProjectPathResolver.ResolveProjectPath(referencePath, includeAllDotnetProjectTypes: true);
                if (Directory.Exists(referencePath))
                {
                    throw new DotnetWatchToolException($"Project reference '{referencePath}' does not resolve to one physical project; watch isolation is unknown.");
                }
                references.Add(Normalize(referencePath));
            }
            var root = properties["MSBuildProjectDirectory"];
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(properties["MSBuildToolsPath"]) ||
                string.IsNullOrEmpty(properties["DOTNET_HOST_PATH"]))
            {
                throw new DotnetWatchToolException("SDK evaluation returned no physical project, SDK, or dotnet host identity.");
            }
            var outputs = new[] { "OutputPath", "OutDir", "BaseIntermediateOutputPath", "IntermediateOutputPath", "MSBuildProjectExtensionsPath" }
                .Select(name => properties.GetValueOrDefault(name))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Normalize(Path.GetFullPath(PathNormalizer.NormalizePathForCurrentPlatform(value!), root)))
                .Distinct(PathComparer)
                .ToArray();
            return new(path, properties["MSBuildToolsPath"], properties["DOTNET_HOST_PATH"], properties, [.. references], outputs);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static string? GetCompatibilityReason(DotnetWatchRoot root, DotnetWatchToolInstallation tool)
    {
        if (root.Reason is not null)
        {
            return root.Reason;
        }
        var project = root.Project;
        var properties = project.Properties;
        if (root.Resource.GetReplicaCount() != 1)
        {
            return "The selected watch protocol cannot attribute per-instance lifecycle events to replicas.";
        }
        if (root.Metadata is DotnetProjectMetadata metadata && metadata.BuildConfiguration is not (null or "" or "Debug"))
        {
            return "The watcher cannot preserve a non-Debug initial build/workspace configuration.";
        }
        if (!string.IsNullOrEmpty(properties["TargetFrameworks"]))
        {
            return "The watcher cannot select the intended framework of a multi-targeted service.";
        }
        if (properties["TargetFrameworkIdentifier"] != ".NETCoreApp")
        {
            return "The selected runtime does not support the managed watch agent.";
        }
        if (!Version.TryParse(properties["TargetFrameworkVersion"].TrimStart('v'), out var targetVersion) ||
            targetVersion.Major < 6)
        {
            return "The selected target framework is older than the managed watch agent.";
        }
        if (properties["OutputType"] is not ("Exe" or "WinExe"))
        {
            return "The selected project is not a managed executable.";
        }
        if (properties["StartupHookSupport"].Equals("false", StringComparison.OrdinalIgnoreCase) ||
            properties["MetadataUpdaterSupport"].Equals("false", StringComparison.OrdinalIgnoreCase) ||
            properties["PublishAot"].Equals("true", StringComparison.OrdinalIgnoreCase) ||
            properties["PublishTrimmed"].Equals("true", StringComparison.OrdinalIgnoreCase) ||
            properties["Optimize"].Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "The selected project configuration disables the managed hot-reload agent.";
        }
        var command = properties["RunCommand"].Trim('"');
        var target = properties["TargetPath"];
        var useAppHost = properties["UseAppHost"].Equals("true", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(command) || string.IsNullOrEmpty(target))
        {
            return "The selected SDK run command could not be established.";
        }
        var standardCommand = useAppHost
            ? PathComparer.Equals(
                Normalize(Path.GetFullPath(command, Path.GetDirectoryName(project.Path)!)),
                Normalize(Path.ChangeExtension(target, OperatingSystem.IsWindows() ? ".exe" : null)))
            : PathComparer.Equals(command, project.DotnetPath) ||
                command.Equals(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", StringComparison.OrdinalIgnoreCase);
        // ComputeRunArguments normally emits either no arguments for the apphost, or
        //   exec "/repo/bin/Debug/net10.0/Service.dll"
        // for the muxer. Do not silently replace a custom SDK launcher/output.
        var runArguments = CommandLineArgsParser.Parse(properties["RunArguments"]);
        var standardArguments = useAppHost
            ? runArguments.Count == 0
            : runArguments.Count == 2 && runArguments[0] == "exec" &&
                PathComparer.Equals(Normalize(runArguments[1]), Normalize(target));
        if (!standardCommand || !standardArguments)
        {
            return "A custom SDK run command or arguments require ordinary execution.";
        }
        var runDirectory = properties["RunWorkingDirectory"];
        if (!string.IsNullOrEmpty(runDirectory) &&
            !PathComparer.Equals(Normalize(Path.GetFullPath(runDirectory, Path.GetDirectoryName(project.Path)!)),
                Normalize(Path.GetDirectoryName(project.Path)!)))
        {
            return "The watcher cannot preserve a custom SDK run working directory.";
        }

        var configPath = Path.Combine(project.SdkDirectory, "MSBuild.runtimeconfig.json");
        if (!File.Exists(configPath))
        {
            return "The selected SDK's MSBuild runtime requirement could not be established.";
        }
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        var options = config.RootElement.GetProperty("runtimeOptions");
        var frameworks = options.TryGetProperty("framework", out var framework)
            ? new[] { framework }
            : options.GetProperty("frameworks").EnumerateArray().ToArray();
        var version = frameworks.First(item => item.GetProperty("name").GetString() == "Microsoft.NETCore.App")
            .GetProperty("version").GetString();
        if (version is null || !Version.TryParse(version.Split('-')[0], out var runtimeVersion))
        {
            return "The selected SDK's MSBuild runtime version is invalid.";
        }
        if (runtimeVersion.Major > tool.RuntimeMajor)
        {
            return $"The selected SDK requires .NET {version}, but the watcher runs on .NET {tool.RuntimeMajor}. Select a compatible explicit watcher version.";
        }

        return null;
    }
}
