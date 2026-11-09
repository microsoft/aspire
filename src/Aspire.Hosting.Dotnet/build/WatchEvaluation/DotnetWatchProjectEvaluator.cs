// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Xml;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>Reads public MSBuild evaluation data in the selected SDK's runtime, without executing targets.</summary>
internal static class DotnetWatchProjectEvaluator
{
    private static int Main()
    {
        var protocolOutput = Console.Out;
        DotnetWatchProjectEvaluationResponse response;
        try
        {
            var input = ReadRequest();
            var request = JsonSerializer.Deserialize(input, DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchProjectEvaluationRequest)
                ?? throw new EvaluationException("The watch graph evaluation request is empty.");
            if (request.Version != DotnetWatchProjectEvaluationProtocol.Version)
            {
                throw new EvaluationException("The watch graph evaluation protocol is incompatible.");
            }

            // SDK resolvers may write diagnostics to Console.Out. Only our typed response belongs on this pipe;
            // evaluation failures are classified below, rather than exposing build-environment values in diagnostics.
            Console.SetOut(TextWriter.Null);
            var graph = Evaluate(request);
            response = new(DotnetWatchProjectEvaluationProtocol.Version, graph, Error: null);
        }
        catch (EvaluationException exception)
        {
            response = new(DotnetWatchProjectEvaluationProtocol.Version, Graph: null, exception.Message);
        }
        catch (Exception exception)
        {
            var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            response = new(
                DotnetWatchProjectEvaluationProtocol.Version,
                Graph: null,
                $"The selected SDK could not expose a complete watch graph ({cause.GetType().Name}); watch build fidelity is unknown.");
        }

        var output = JsonSerializer.Serialize(response, DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchProjectEvaluationResponse);
        if (output.Length > DotnetWatchProjectEvaluationProtocol.MaxResponseLength)
        {
            output = JsonSerializer.Serialize(
                new DotnetWatchProjectEvaluationResponse(DotnetWatchProjectEvaluationProtocol.Version, Graph: null, "The evaluated watch graph exceeds the supported response size."),
                DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchProjectEvaluationResponse);
        }
        protocolOutput.Write(output);
        protocolOutput.Flush();

        return response.Error is null ? 0 : 1;
    }

    private static string ReadRequest()
    {
        var input = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = Console.In.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (input.Length + count > DotnetWatchProjectEvaluationProtocol.MaxRequestLength)
            {
                throw new EvaluationException("The watch graph evaluation request exceeds the supported size.");
            }
            input.Append(buffer, 0, count);
        }

        return input.ToString();
    }

    private static DotnetWatchProjectGraphResult Evaluate(DotnetWatchProjectEvaluationRequest request)
    {
        var sdkDirectory = Path.GetFullPath(request.SdkDirectory);
        // MSBuild's SDK resolver and toolset initialization read these before their assemblies are loaded.
        // Bind exactly one SDK in this fresh process; never register an SDK in the AppHost's load context.
        Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", Path.Combine(sdkDirectory, "MSBuild.dll"));
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdkDirectory, "Sdks"));
        Environment.SetEnvironmentVariable("MSBuildExtensionsPath", sdkDirectory);
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", request.Muxer);
        Environment.SetEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", Path.GetDirectoryName(request.Muxer));
        Environment.SetEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", Path.Combine(sdkDirectory, "Sdks"));
        Environment.SetEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER", request.SdkVersion);
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        // NuGet's SDK resolver can restore a missing SDK during plain ProjectInstance evaluation. Preflight
        // must never acquire packages or become an implicit restore: package-only SDK contexts are unsupported.
        // https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/Microsoft.Build.NuGetSdkResolver/NuGetSdkResolver.cs
        Environment.SetEnvironmentVariable("MSBUILDDISABLENUGETSDKRESOLVER", "1");

        // The adapter has BCL-only references. Public API reflection lets the SDK provide its own MSBuild
        // assembly identity and dependencies, even when Aspire was built using a different SDK.
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(sdkDirectory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var build = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(sdkDirectory, "Microsoft.Build.dll"));
        var instanceType = GetType(build, "Microsoft.Build.Execution.ProjectInstance");
        var collectionType = GetType(build, "Microsoft.Build.Evaluation.ProjectCollection");
        var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.GlobalProperties)
        {
            var escape = GetMethod(collectionType, "Escape", [typeof(string)]);
            if (!string.IsNullOrEmpty(request.Configuration))
            {
                globalProperties["Configuration"] = (string)escape.Invoke(null, [request.Configuration])!;
            }
            foreach (var (name, value) in request.Environment)
            {
                VerifyPropertyName(name);
                // MSBuild global properties use escaped values; the SDK unescapes them once during evaluation.
                // For example, a literal "one;two%3B$(Other)" must not become a list or a property expansion.
                globalProperties[name] = (string)escape.Invoke(null, [value])!;
            }
        }

        using var collection = (IDisposable)(Activator.CreateInstance(collectionType, globalProperties)
            ?? throw new EvaluationException("The selected SDK cannot create an isolated project collection."));
        var getValue = GetMethod(instanceType, "GetPropertyValue", [typeof(string)]);
        var getItems = GetMethod(instanceType, "GetItems", [typeof(string)]);
        var importsProperty = instanceType.GetProperty("ImportPaths")
            ?? throw new EvaluationException("The selected SDK does not expose public evaluated import paths.");
        var fullPathProperty = instanceType.GetProperty("FullPath")
            ?? throw new EvaluationException("The selected SDK does not expose physical project paths.");
        var constructor = instanceType.GetConstructor([typeof(string), typeof(IDictionary<string, string>), typeof(string), collectionType])
            ?? throw new EvaluationException("The selected SDK does not support isolated project evaluation.");
        var projects = new Dictionary<string, DotnetWatchProjectNode>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var evaluatedFrameworks = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var rootPath = NormalizePhysicalPath(request.ProjectPath);
        var pending = new Queue<(string Path, string? TargetFramework)>();
        pending.Enqueue((rootPath, TargetFramework: null));
        var propertyNames = DotnetWatchProjectEvaluationProtocol.PropertyNames.Concat(request.Environment.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Type? virtualBuilder = null;
        while (pending.TryDequeue(out var work))
        {
            var (path, requestedTargetFramework) = work;
            if (projects.ContainsKey(path))
            {
                if (evaluatedFrameworks[path] is { } evaluatedTargetFramework &&
                    requestedTargetFramework is not null &&
                    !string.Equals(evaluatedTargetFramework, requestedTargetFramework, StringComparison.OrdinalIgnoreCase))
                {
                    throw new EvaluationException(
                        $"Project '{path}' is required in multiple target-framework contexts; watch output isolation is unknown.");
                }
                continue;
            }
            if (projects.Count >= DotnetWatchProjectEvaluationProtocol.MaxProjects)
            {
                throw new EvaluationException("The evaluated watch graph exceeds the supported project count.");
            }

            object instance;
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // Use the same SDK implementation as dotnet build/watch. It expands directives, resolves imported
                // references, and visits included C# sources; no handwritten #: parser or generated XML replica.
                // https://github.com/dotnet/sdk/blob/v10.0.403/src/Microsoft.DotNet.ProjectTools/VirtualProjectBuilder.cs
                virtualBuilder ??= GetVirtualBuilder(sdkDirectory);
                if (!int.TryParse(request.SdkVersion.AsSpan(0, request.SdkVersion.IndexOf('.')), out var major))
                {
                    throw new EvaluationException("The selected SDK has an unrecognized default file-app framework.");
                }
                var errors = new List<string>();
                Action<string, int, string> report = (source, line, _) =>
                {
                    if (errors.Count < 8)
                    {
                        errors.Add($"'{source}' line {line}");
                    }
                };
                instance = GetMethod(virtualBuilder, "CreateProjectInstance", [typeof(string), typeof(string), collectionType, typeof(Action<string, int, string>)])
                    .Invoke(null, [path, $"net{major}.0", collection, report])!;
                if (errors.Count > 0)
                {
                    throw new EvaluationException($"The SDK rejected file-app directives at {string.Join(", ", errors)}; watch build fidelity is unknown.");
                }
            }
            else
            {
                if (!File.Exists(path))
                {
                    throw new EvaluationException($"Project '{path}' is not one physical project; watch output isolation is unknown.");
                }
                instance = constructor.Invoke([path, globalProperties, null, collection]);
            }

            var properties = ReadProperties(instance);
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(properties["TargetFrameworks"]) &&
                string.IsNullOrEmpty(properties["TargetFramework"]))
            {
                if (requestedTargetFramework is null)
                {
                    var role = path == rootPath ? "runnable root" : "referenced project";
                    throw new EvaluationException(
                        $"Multi-targeted {role} '{path}' requires an explicit target framework; watch output isolation is unknown.");
                }

                var targetedProperties = new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase)
                {
                    ["TargetFramework"] = requestedTargetFramework
                };
                instance = constructor.Invoke([path, targetedProperties, null, collection]);
                properties = ReadProperties(instance);
                if (string.IsNullOrEmpty(properties["TargetFramework"]))
                {
                    throw new EvaluationException(
                        $"Project '{path}' could not be evaluated in parent-selected framework '{requestedTargetFramework}'.");
                }
            }
            evaluatedFrameworks[path] = string.IsNullOrEmpty(properties["TargetFrameworks"])
                ? null
                : properties["TargetFramework"];

            var compileItems = ((IEnumerable)getItems.Invoke(instance, ["Compile"])!).Cast<object>()
                .Select(item => new DotnetWatchCompileItem(
                    (string)item.GetType().GetProperty("EvaluatedInclude")!.GetValue(item)!,
                    (string)GetMethod(item.GetType(), "GetMetadataValue", [typeof(string)]).Invoke(item, ["FullPath"])!))
                .OrderBy(item => item.FullPath, StringComparer.Ordinal)
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .ToArray();
            var centralVersions = ((IEnumerable)getItems.Invoke(instance, ["PackageVersion"])!).Cast<object>()
                .Select(item => (
                    Identity: (string)item.GetType().GetProperty("EvaluatedInclude")!.GetValue(item)!,
                    Version: (string)GetMethod(item.GetType(), "GetMetadataValue", [typeof(string)]).Invoke(item, ["Version"])!))
                .ToLookup(item => item.Identity, item => item.Version, StringComparer.OrdinalIgnoreCase);
            var packages = ((IEnumerable)getItems.Invoke(instance, ["PackageReference"])!).Cast<object>()
                .Select(item =>
                {
                    var identity = (string)item.GetType().GetProperty("EvaluatedInclude")!.GetValue(item)!;
                    var metadata = GetMethod(item.GetType(), "GetMetadataValue", [typeof(string)]);

                    return new DotnetWatchPackageReference(identity,
                        (string)metadata.Invoke(item, ["Version"])!,
                        (string)metadata.Invoke(item, ["VersionOverride"])!,
                        [.. centralVersions[identity].Order(StringComparer.Ordinal)]);
                })
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ThenBy(item => item.VersionOverride, StringComparer.Ordinal)
                .ToArray();
            properties["CompileItems"] = JsonSerializer.Serialize(compileItems, DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchCompileItemArray);
            properties["PackageReferences"] = JsonSerializer.Serialize(packages, DotnetWatchProjectEvaluationJsonContext.Default.DotnetWatchPackageReferenceArray);
            ValidateRunArguments(instanceType, instance, path);
            var fullPath = (string)fullPathProperty.GetValue(instance)!;
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(properties["MSBuildProjectDirectory"]))
            {
                throw new EvaluationException($"The SDK returned no physical project context for '{path}'.");
            }

            var references = new List<string>();
            foreach (var reference in (IEnumerable)getItems.Invoke(instance, ["ProjectReference"])!)
            {
                var metadata = GetMethod(reference.GetType(), "GetMetadataValue", [typeof(string)]);
                foreach (var name in new[] { "AdditionalProperties", "SetConfiguration", "SetPlatform", "Targets", "GlobalPropertiesToRemove" })
                {
                    var setting = (string)metadata.Invoke(reference, [name])!;
                    if (string.IsNullOrEmpty(setting))
                    {
                        continue;
                    }
                    // The Web SDK adds ";WebPublishProfileFile" even to ordinary project references.
                    // Removing an unset profile changes no build context; any other removal is unrepresentable.
                    // https://github.com/dotnet/sdk/blob/v10.0.403/src/WebSdk/Publish/Targets/Microsoft.NET.Sdk.Publish.targets
                    if (name == "GlobalPropertiesToRemove" && string.IsNullOrEmpty(properties["WebPublishProfileFile"]) &&
                        setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .All(value => value.Equals("WebPublishProfileFile", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    throw new EvaluationException($"A project reference in '{path}' changes its build context through '{name}'; watch output isolation is unknown.");
                }
                var referenceTargetFramework = ParseTargetFramework(
                    (string)metadata.Invoke(reference, ["SetTargetFramework"])!) ?? properties["TargetFramework"];
                var referencePath = (string)metadata.Invoke(reference, ["FullPath"])!;
                if (string.IsNullOrEmpty(referencePath) || Directory.Exists(referencePath))
                {
                    throw new EvaluationException($"A project reference in '{path}' does not identify one physical project.");
                }
                if (!File.Exists(referencePath) && referencePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    virtualBuilder ??= GetVirtualBuilder(sdkDirectory);
                    object?[] arguments = [referencePath, null];
                    if ((bool)GetMethod(virtualBuilder, "TryGetEntryPointFilePathFromVirtualProjectPath", [typeof(string), typeof(string).MakeByRefType()])
                        .Invoke(null, arguments)!)
                    {
                        referencePath = (string)arguments[1]!;
                    }
                }
                referencePath = NormalizePhysicalPath(referencePath);
                references.Add(referencePath);
                pending.Enqueue((referencePath, referenceTargetFramework));
            }

            var imports = ((IEnumerable)importsProperty.GetValue(instance)!).Cast<string>().ToList();
            // ImportPaths omits the project itself. A file app's FullPath names a virtual project, so record its
            // physical entry point instead, plus the SDK-evaluated Compile inputs that can contain directives.
            imports.Add(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? path : fullPath);
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                imports.AddRange(compileItems.Select(item => item.FullPath));
            }
            var outputs = DotnetWatchProjectEvaluationProtocol.OutputPropertyNames
                .Select(name => properties[name])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (outputs.Length == 0)
            {
                throw new EvaluationException($"Project '{path}' exposes no evaluated output domain; watch output isolation is unknown.");
            }
            projects.Add(path, new(path, properties, [.. references], [.. imports], outputs));
        }

        return new(sdkDirectory, request.Muxer, request.SdkVersion, projects);

        Dictionary<string, string> ReadProperties(object projectInstance) =>
            propertyNames.ToDictionary(
                name => name,
                name => (string)getValue.Invoke(projectInstance, [name])!,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? ParseTargetFramework(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string prefix = "TargetFramework=";
        var assignment = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SingleOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return assignment?[prefix.Length..];
    }

    private static void ValidateRunArguments(Type instanceType, object instance, string path)
    {
        var targets = instanceType.GetProperty("Targets")?.GetValue(instance) as IDictionary
            ?? throw new EvaluationException("The selected SDK cannot expose run-command target definitions.");
        foreach (DictionaryEntry entry in targets)
        {
            var target = entry.Value!;
            var targetType = target.GetType();
            var name = (string)entry.Key;
            if (name.Equals("ComputeRunArguments", StringComparison.OrdinalIgnoreCase))
            {
                var children = targetType.GetProperty("Children")?.GetValue(target) as ICollection;
                var dependencies = targetType.GetProperty("DependsOnTargets")?.GetValue(target) as string;
                if (children is null || children.Count != 0 || !string.IsNullOrWhiteSpace(dependencies))
                {
                    throw new EvaluationException($"Project '{path}' requires executing a custom ComputeRunArguments target; watch launch fidelity cannot be established without running targets.");
                }
            }
            foreach (var hook in new[] { "BeforeTargets", "AfterTargets" })
            {
                var value = targetType.GetProperty(hook)?.GetValue(target) as string ?? string.Empty;
                var expanded = (string)GetMethod(instanceType, "ExpandString", [typeof(string)]).Invoke(instance, [value])!;
                if (expanded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains("ComputeRunArguments", StringComparer.OrdinalIgnoreCase))
                {
                    throw new EvaluationException($"Project '{path}' attaches a custom target to ComputeRunArguments; watch launch fidelity cannot be established without running targets.");
                }
            }
        }
    }

    private static Type GetVirtualBuilder(string sdkDirectory)
    {
        var path = Path.Combine(sdkDirectory, "Microsoft.DotNet.ProjectTools.dll");
        if (!File.Exists(path))
        {
            throw new EvaluationException("The selected SDK does not expose evaluated file-app projects.");
        }

        return GetType(AssemblyLoadContext.Default.LoadFromAssemblyPath(path), "Microsoft.DotNet.ProjectTools.VirtualProjectBuilder");
    }

    private static Type GetType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: false) is { IsPublic: true } type
            ? type
            : throw new EvaluationException($"The selected SDK does not expose public '{name}' evaluation.");

    private static MethodInfo GetMethod(Type type, string name, Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, parameters)
            ?? throw new EvaluationException($"The selected SDK does not expose public '{type.Name}.{name}' evaluation.");

    private static void VerifyPropertyName(string name)
    {
        try
        {
            XmlConvert.VerifyNCName(name);
        }
        catch (XmlException)
        {
            throw new EvaluationException($"Build environment name '{name}' cannot be represented as an MSBuild global property.");
        }
    }

    private static string NormalizePhysicalPath(string path) =>
        PathNormalizer.ResolveToFilesystemPath(Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    private sealed class EvaluationException(string message) : Exception(message);
}
