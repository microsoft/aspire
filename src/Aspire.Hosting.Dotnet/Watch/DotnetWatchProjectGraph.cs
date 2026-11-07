// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Utils;
using Aspire.Shared.DotnetWatch;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>Evaluates watch build fidelity and output isolation using an SDK-bound child process.</summary>
internal static class DotnetWatchProjectGraph
{
    /// <summary>Compares filesystem-canonical project identities without folding case-sensitive macOS volumes.</summary>
    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Resolves separators, symbolic links, and the existing filesystem's path casing.</summary>
    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(PathNormalizer.ResolveToFilesystemPath(Path.GetFullPath(ReplaceSeparators(path))));

    /// <summary>Evaluates physical roots, references, active imports, and build outputs without building or restoring.</summary>
    internal static async Task<DotnetWatchProjectGraphResult> EvaluateGraphAsync(
        string projectPath,
        string? configuration,
        IReadOnlyDictionary<string, string> environment,
        bool globalProperties,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Normalize(workingDirectory);
        var path = NormalizeRelative(projectPath, directory);
        var sdkScopeDirectory = Path.GetDirectoryName(path)!;
        var effectiveEnvironment = environment.Where(pair => !DotnetWatchOwnership.IsPrivateEnvironmentVariable(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (effectiveEnvironment.Keys.Any(name => name.Equals("CompileItems", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PackageReferences", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DistributedApplicationException("The build environment conflicts with watch graph item metadata; watch build fidelity is unknown.");
        }
        var payloadDirectory = DotnetWatchToolResolver.GetWatchHostDirectory(Assembly.GetEntryAssembly());
        var adapterPath = payloadDirectory is null ? null : Path.Combine(payloadDirectory, DotnetWatchProjectEvaluationProtocol.AssemblyFileName);
        var sdkQueryPath = payloadDirectory is null ? null : Path.Combine(payloadDirectory, DotnetWatchProjectEvaluationProtocol.SdkQueryFileName);
        if (adapterPath is null || !File.Exists(adapterPath) || sdkQueryPath is null || !File.Exists(sdkQueryPath))
        {
            throw new DistributedApplicationException(
                "The bundled watch graph evaluation adapter is missing. Restore and rebuild the AppHost with Aspire.Hosting.Dotnet, or run without watch.");
        }
        // The upstream watcher binds --sdk from the root, independently of its startup cwd. Query a bare project
        // there: no service evaluation/targets, no response files, and no ambiguity when two SDK installs share a version.
        var sdkInfo = CreateStartInfo("dotnet", sdkScopeDirectory, effectiveEnvironment, configuration, globalProperties);
        foreach (var argument in new[]
        {
            "msbuild", sdkQueryPath, "-nologo", "-v:q", "-noAutoResponse", "-getProperty:MSBuildToolsPath,DOTNET_HOST_PATH"
        })
        {
            sdkInfo.ArgumentList.Add(argument);
        }
        var sdkOutput = await RunAsync(sdkInfo, input: null, "SDK selection", cancellationToken).ConfigureAwait(false);
        string sdkDirectory;
        string muxer;
        try
        {
            // MSBuild's no-target property query emits { "Properties": { "MSBuildToolsPath": "...",
            // "DOTNET_HOST_PATH": "..." } }. Unlike SDK inventory text, this identifies the actual selected paths.
            using var sdkDocument = JsonDocument.Parse(sdkOutput.Output);
            var properties = sdkDocument.RootElement.GetProperty("Properties");
            sdkDirectory = Normalize(properties.GetProperty("MSBuildToolsPath").GetString()!);
            muxer = Normalize(properties.GetProperty("DOTNET_HOST_PATH").GetString()!);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new DistributedApplicationException($"Could not establish the root-selected SDK and dotnet host for '{path}'.", exception);
        }
        var version = Path.GetFileName(Path.TrimEndingDirectorySeparator(sdkDirectory));
        ValidateSdkOverrides(effectiveEnvironment, sdkDirectory, muxer, version);
        ValidateResponseFiles(directory, sdkDirectory);
        foreach (var file in new[] { "MSBuild.runtimeconfig.json", "MSBuild.deps.json", "Microsoft.Build.dll" })
        {
            if (!File.Exists(Path.Combine(sdkDirectory, file)))
            {
                throw new DistributedApplicationException($"SDK '{version}' has no '{file}'; watch build fidelity cannot be established.");
            }
        }

        var request = new DotnetWatchProjectEvaluationRequest(
            DotnetWatchProjectEvaluationProtocol.Version, path, configuration, effectiveEnvironment, globalProperties, sdkDirectory, muxer, version);
        var input = JsonSerializer.Serialize(request, DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchProjectEvaluationRequest);
        if (input.Length > DotnetWatchProjectEvaluationProtocol.MaxRequestLength)
        {
            throw new DistributedApplicationException("The watch build context exceeds the supported evaluation request size.");
        }
        var startInfo = CreateStartInfo(muxer, directory, effectiveEnvironment, configuration, globalProperties);
        startInfo.RedirectStandardInput = true;
        // Console.In preserves a UTF-8 preamble, which JsonSerializer's string input cannot accept.
        startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.Combine(sdkDirectory, "MSBuild.runtimeconfig.json"));
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(Path.Combine(sdkDirectory, "MSBuild.deps.json"));
        startInfo.ArgumentList.Add(adapterPath);
        var result = await RunAsync(startInfo, input, "project graph evaluation", cancellationToken, acceptDiagnosedFailure: true).ConfigureAwait(false);
        DotnetWatchProjectEvaluationResponse response;
        try
        {
            // The adapter emits one typed JSON response on stdout, with SDK console output suppressed:
            // { "Version": 1, "Graph": { "SdkDirectory": "...", "Projects": { "/repo/Api.csproj": { ... } } }, "Error": null }
            // Input and output use pipes, not temporary build files or response files containing environment values.
            response = JsonSerializer.Deserialize(result.Output, DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchProjectEvaluationResponse)
                ?? throw new JsonException("Empty evaluation response.");
        }
        catch (JsonException exception)
        {
            throw new DistributedApplicationException($"SDK '{version}' returned an invalid watch graph evaluation response for '{path}'.", exception);
        }
        if (response.Version != DotnetWatchProjectEvaluationProtocol.Version || response.Error is { Length: > 0 } ||
            response.Graph is null || result.ExitCode != 0)
        {
            throw new DistributedApplicationException(response.Error ?? $"SDK '{version}' could not evaluate a complete watch graph for '{path}'.");
        }
        if (!PathComparer.Equals(Normalize(response.Graph.SdkDirectory), sdkDirectory) ||
            !PathComparer.Equals(Normalize(response.Graph.Muxer), muxer) || response.Graph.SdkVersion != version)
        {
            throw new DistributedApplicationException("The watch graph evaluator changed the selected SDK or dotnet host.");
        }

        return NormalizeGraph(response.Graph, path);
    }

    /// <summary>Reports shared physical projects or overlapping evaluated output/intermediate domains.</summary>
    internal static bool SharesOutputs(DotnetWatchProjectGraphResult leftResult, DotnetWatchProjectGraphResult rightResult)
    {
        if (leftResult.Projects.Keys.Any(left => rightResult.Projects.Keys.Contains(left, PathComparer)))
        {
            return true;
        }

        return leftResult.Projects.Values.SelectMany(project => project.Outputs)
            .Any(left => rightResult.Projects.Values.SelectMany(project => project.Outputs)
                .Any(right => ContainsPath(left, right) || ContainsPath(right, left)));
    }

    /// <summary>Checks whether a refresh preserves SDK, physical graph, output, and evaluated build/launch context.</summary>
    internal static bool Equivalent(DotnetWatchProjectGraphResult leftResult, DotnetWatchProjectGraphResult rightResult)
    {
        if (!PathComparer.Equals(leftResult.SdkDirectory, rightResult.SdkDirectory) ||
            !PathComparer.Equals(leftResult.Muxer, rightResult.Muxer) || leftResult.SdkVersion != rightResult.SdkVersion ||
            leftResult.Projects.Count != rightResult.Projects.Count)
        {
            return false;
        }
        foreach (var (path, left) in leftResult.Projects)
        {
            if (!rightResult.Projects.TryGetValue(path, out var right) || !PathComparer.Equals(left.Path, right.Path) ||
                !new HashSet<string>(left.References, PathComparer).SetEquals(right.References) ||
                !new HashSet<string>(left.Outputs, PathComparer).SetEquals(right.Outputs) ||
                left.Properties.Count != right.Properties.Count ||
                left.Properties.Any(property => !IsRefreshMetadata(property.Key) &&
                    (!right.Properties.TryGetValue(property.Key, out var value) || property.Value != value)))
            {
                return false;
            }
        }

        // Imports can legitimately change while the evaluated context is unchanged. The caller must rebuild its
        // observed-input inventory and confirm old watcher teardown; this is not permission for live repartition.
        return true;
    }

    private static DotnetWatchProjectGraphResult NormalizeGraph(DotnetWatchProjectGraphResult graph, string root)
    {
        if (graph.Projects.Count == 0 || graph.Projects.Count > DotnetWatchProjectEvaluationProtocol.MaxProjects)
        {
            throw new DistributedApplicationException("The watch graph evaluator returned an invalid project inventory.");
        }
        var projects = new Dictionary<string, DotnetWatchProjectNode>(PathComparer);
        foreach (var (_, project) in graph.Projects)
        {
            var path = Normalize(project.Path);
            var directory = Path.GetDirectoryName(path)!;
            var references = project.References.Select(reference => NormalizeRelative(reference, directory)).Distinct(PathComparer).ToArray();
            var imports = project.Imports.Select(import => NormalizeRelative(import, directory)).Distinct(PathComparer).ToArray();
            var outputs = project.Outputs.Select(output => NormalizeRelative(output, directory)).Distinct(PathComparer).ToArray();
            var properties = new Dictionary<string, string>(project.Properties, StringComparer.OrdinalIgnoreCase);
            try
            {
                var compileItems = JsonSerializer.Deserialize(properties["CompileItems"], DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchCompileItemArray)
                    ?? throw new JsonException("Empty compile item inventory.");
                properties["CompileItems"] = JsonSerializer.Serialize(
                    compileItems.Select(item => item with { FullPath = NormalizeRelative(item.FullPath, directory) })
                        .OrderBy(item => item.FullPath, StringComparer.Ordinal).ThenBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
                    DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchCompileItemArray);
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or ArgumentException)
            {
                throw new DistributedApplicationException($"Project '{path}' returned an invalid evaluated item inventory.", exception);
            }
            var normalized = new DotnetWatchProjectNode(path, properties, references, imports, outputs);
            if (!projects.TryAdd(path, normalized) && !Equivalent(
                new(graph.SdkDirectory, graph.Muxer, graph.SdkVersion, new Dictionary<string, DotnetWatchProjectNode>(PathComparer) { [path] = projects[path] }),
                new(graph.SdkDirectory, graph.Muxer, graph.SdkVersion, new Dictionary<string, DotnetWatchProjectNode>(PathComparer) { [path] = normalized })))
            {
                throw new DistributedApplicationException($"Project '{path}' has multiple evaluated watch build contexts.");
            }
        }
        if (!projects.ContainsKey(root) || projects.Values.Any(project => project.References.Any(reference => !projects.ContainsKey(reference))))
        {
            throw new DistributedApplicationException("The watch graph evaluator returned an incomplete physical reference graph.");
        }
        var visited = new HashSet<string>(PathComparer);
        var active = new HashSet<string>(PathComparer);
        foreach (var path in projects.Keys)
        {
            Visit(path);
        }

        return new(Normalize(graph.SdkDirectory), Normalize(graph.Muxer), graph.SdkVersion, projects);

        void Visit(string path)
        {
            if (active.Contains(path))
            {
                throw new DistributedApplicationException($"Project '{path}' has a cyclic reference graph; watch build fidelity is unknown.");
            }
            if (!visited.Add(path))
            {
                return;
            }
            active.Add(path);
            foreach (var reference in projects[path].References)
            {
                Visit(reference);
            }
            active.Remove(path);
        }
    }

    private static string NormalizeRelative(string path, string directory) =>
        // Anchor MSBuild's relative paths before using the shared helper, which otherwise resolves against process cwd.
        Normalize(Path.GetFullPath(ReplaceSeparators(path), directory));

    private static string ReplaceSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static bool IsRefreshMetadata(string name) =>
        name.Equals("MSBuildStartupDirectory", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CompileItems", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PackageReferences", StringComparison.OrdinalIgnoreCase);

    private static void ValidateSdkOverrides(IReadOnlyDictionary<string, string> environment, string sdkDirectory, string muxer, string version)
    {
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILD_EXE_PATH"] = Path.Combine(sdkDirectory, "MSBuild.dll"),
            ["MSBuildSDKsPath"] = Path.Combine(sdkDirectory, "Sdks"),
            ["MSBuildExtensionsPath"] = sdkDirectory,
            ["DOTNET_HOST_PATH"] = muxer,
            ["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = Path.GetDirectoryName(muxer)!,
            ["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"] = Path.Combine(sdkDirectory, "Sdks"),
        };
        foreach (var (name, expected) in bindings)
        {
            if (environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) &&
                (!Path.IsPathFullyQualified(ReplaceSeparators(value)) || !PathComparer.Equals(Normalize(value), Normalize(expected))))
            {
                throw new DistributedApplicationException($"Build environment '{name}' replaces the root-selected SDK context; watch build fidelity is unknown.");
            }
        }
        if (environment.TryGetValue("DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER", out var sdkVersion) &&
            !string.IsNullOrEmpty(sdkVersion) && sdkVersion != version)
        {
            throw new DistributedApplicationException("Build environment 'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER' replaces the root-selected SDK context; watch build fidelity is unknown.");
        }
    }

    private static void ValidateResponseFiles(string workingDirectory, string sdkDirectory)
    {
        var sdkResponse = Path.Combine(sdkDirectory, "MSBuild.rsp");
        if (File.Exists(sdkResponse))
        {
            throw new DistributedApplicationException($"SDK response file '{sdkResponse}' is not represented by watcher in-process evaluation; watch build fidelity is unknown.");
        }
        for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
        {
            var response = Path.Combine(directory.FullName, "Directory.Build.rsp");
            if (File.Exists(response))
            {
                throw new DistributedApplicationException($"Build response file '{response}' is not represented by watcher in-process evaluation; watch build fidelity is unknown.");
            }
        }
    }

    private static bool ContainsPath(string directory, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (OperatingSystem.IsMacOS() && (!Path.Exists(directory) || !Path.Exists(path)))
        {
            // A not-yet-built macOS output has no on-disk leaf casing to canonicalize. Conservatively group case
            // aliases rather than guessing that a volume is case-sensitive and allowing concurrent output writers.
            comparison = StringComparison.OrdinalIgnoreCase;
        }

        return string.Equals(directory, path, comparison) ||
            path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar, comparison);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        string directory,
        IReadOnlyDictionary<string, string> environment,
        string? configuration,
        bool globalProperties)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!globalProperties && !string.IsNullOrEmpty(configuration))
        {
            startInfo.Environment["Configuration"] = configuration;
        }
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }
        foreach (var name in startInfo.Environment.Keys.Where(DotnetWatchOwnership.IsPrivateEnvironmentVariable).ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        return startInfo;
    }

    private static async Task<(string Output, int ExitCode)> RunAsync(
        ProcessStartInfo startInfo,
        string? input,
        string operation,
        CancellationToken cancellationToken,
        bool acceptDiagnosedFailure = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new DistributedApplicationException($"Could not start '{startInfo.FileName}' for watch {operation}.", exception);
        }

        // Drain both streams concurrently and cap retained data. A noisy SDK cannot deadlock its own evaluation
        // or make the AppHost retain unbounded diagnostics; build-environment values are never included in errors.
        var output = ReadBoundedAsync(process.StandardOutput, DotnetWatchProjectEvaluationProtocol.MaxResponseLength, timeout.Token);
        var error = ReadBoundedAsync(process.StandardError, 64 * 1024, timeout.Token);
        var write = input is null ? Task.CompletedTask : WriteRequestAsync(process.StandardInput, input, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error, write).ConfigureAwait(false);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            await Task.WhenAll(output, error, write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellationToken.ThrowIfCancellationRequested();
            throw new DistributedApplicationException($"Timed out during watch {operation}; build fidelity is unknown.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TryKill(process);
            await Task.WhenAll(output, error, write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw new DistributedApplicationException($"Watch {operation} could not exchange its evaluation data; build fidelity is unknown.", exception);
        }
        var captured = await output.ConfigureAwait(false);
        if (captured.Exceeded || (!acceptDiagnosedFailure && process.ExitCode != 0))
        {
            throw new DistributedApplicationException($"Watch {operation} failed with exit code {process.ExitCode}; build fidelity is unknown.");
        }

        return (captured.Text, process.ExitCode);
    }

    private static async Task WriteRequestAsync(StreamWriter writer, string input, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
        writer.Close();
    }

    private static async Task<(string Text, bool Exceeded)> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        var exceeded = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            var remaining = limit - output.Length;
            output.Append(buffer, 0, Math.Min(count, remaining));
            exceeded |= count > remaining;
        }

        return (output.ToString(), exceeded);
    }

    private static void TryKill(Process process)
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
            // Preserve the original cancellation/failure if the isolated evaluator exited concurrently.
        }
    }
}
