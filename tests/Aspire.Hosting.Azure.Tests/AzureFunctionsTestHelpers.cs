// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Publishing;

#pragma warning disable ASPIRECONTAINERRUNTIME001 // Exercise the configured local container runtime.

namespace Aspire.Hosting.Azure.Tests;

internal static class AzureFunctionsTestHelpers
{
    private static readonly MethodInfo s_polyglotWithReferenceMethod = typeof(ResourceBuilderExtensions)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == nameof(ResourceBuilderExtensions.WithReference)
            && method.IsGenericMethodDefinition
            && method.GetParameters() is { Length: 5 } parameters
            && parameters[1].ParameterType == typeof(IResourceBuilder<IResource>));

    public static string CreateProject(string directory, string? commandLineArgs = null, string name = "Functions")
    {
        var projectDirectory = Directory.CreateDirectory(Path.Combine(directory, name)).FullName;
        var projectPath = Path.Combine(projectDirectory, $"{name}.csproj");
        File.WriteAllText(projectPath, """<Project Sdk="Microsoft.NET.Sdk" />""");
        WriteLaunchSettings(projectPath, new LaunchSettings
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["Functions"] = new()
                {
                    CommandName = "Project",
                    CommandLineArgs = commandLineArgs,
                    LaunchBrowser = false
                }
            }
        });

        return projectPath;
    }

    public static void WriteLaunchSettings(string projectPath, LaunchSettings launchSettings)
    {
        var properties = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(projectPath)!, "Properties"));
        File.WriteAllText(Path.Combine(properties.FullName, "launchSettings.json"), JsonSerializer.Serialize(launchSettings));
    }

    public static void ConfigureDebugSession(IDistributedApplicationBuilder builder, string[]? capabilities)
    {
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = capabilities is null
            ? null
            : JsonSerializer.Serialize(new
            {
                protocols_supported = new[] { "test" },
                supported_launch_configurations = capabilities
            });
    }

    public static IResourceBuilder<TResource> InvokeWithReference<TResource>(
        IResourceBuilder<TResource> destination,
        IResourceBuilder<IResource> source,
        string? connectionName = null,
        bool optional = false,
        string? name = null)
        where TResource : IResourceWithEnvironment
    {
        return (IResourceBuilder<TResource>)s_polyglotWithReferenceMethod.MakeGenericMethod(typeof(TResource))
            .Invoke(null, [destination, source, connectionName, optional, name])!;
    }

    public static async Task<Dictionary<string, object>> GetEnvironmentAsync(
        IDistributedApplicationBuilder builder,
        IResourceWithEnvironment resource)
    {
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return context.EnvironmentVariables;
    }

    public static string CreateRuntimeProject(string directory)
    {
        var projectDirectory = Directory.CreateDirectory(Path.Combine(directory, "Functions")).FullName;
        // Isolate generated builds from whichever repository contains the workspace, while retaining the
        // same package versions the test project evaluated. WorkerExtensions performs a nested restore.
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(directory, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(directory, "Directory.Packages.props"), "<Project />");
        var assembly = typeof(AzureFunctionsTestHelpers).Assembly;
        var framework = new FrameworkName(assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        var packageVersions = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key.StartsWith("FunctionsRuntimePackage.", StringComparison.Ordinal))
            .Select(attribute => new XElement("PackageReference",
                new XAttribute("Include", attribute.Key["FunctionsRuntimePackage.".Length..]),
                new XAttribute("Version", attribute.Value!)))
            .ToArray();
        if (packageVersions.Length != 4)
        {
            throw new InvalidOperationException("The Functions runtime fixture requires the four evaluated Functions package versions.");
        }

        var project = new XElement("Project",
            new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", $"net{framework.Version.Major}.{framework.Version.Minor}"),
                new XElement("OutputType", "Exe"),
                new XElement("AzureFunctionsVersion", "v4"),
                new XElement("ImplicitUsings", "enable"),
                new XElement("Nullable", "enable"),
                new XElement("ManagePackageVersionsCentrally", "false"),
                new XElement("OutputPath", Path.Combine("$(MSBuildProjectDirectory)", "bin", "$(Configuration)", "$(BUILD_FLAVOR)") + Path.DirectorySeparatorChar)),
            new XElement("ItemGroup",
                new XElement("FrameworkReference", new XAttribute("Include", "Microsoft.AspNetCore.App")),
                new XElement("AssemblyMetadata", new XAttribute("Include", "BuildFlavor"), new XAttribute("Value", "$(BUILD_FLAVOR)")),
                packageVersions),
            new XElement("ItemGroup",
                new XElement("None", new XAttribute("Update", "host.json"), new XAttribute("CopyToOutputDirectory", "PreserveNewest"))),
            new XElement("Target",
                new XAttribute("Name", "RecordFunctionsBuild"),
                new XAttribute("AfterTargets", "Build"),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", Path.Combine("$(MSBuildProjectDirectory)", "build-count.txt")),
                    new XAttribute("Lines", "built"),
                    new XAttribute("Overwrite", "false"))));
        var projectPath = Path.Combine(projectDirectory, "Functions.csproj");
        File.WriteAllText(projectPath, project.ToString());
        File.WriteAllText(Path.Combine(projectDirectory, "host.json"), """{"version":"2.0"}""");
        File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), """
            using Microsoft.Extensions.Hosting;

            var host = new HostBuilder().ConfigureFunctionsWebApplication().Build();
            await host.RunAsync().ConfigureAwait(false);
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Functions.cs"), """
            using System.Net;
            using System.Reflection;
            using Microsoft.Azure.Functions.Worker;
            using Microsoft.Azure.Functions.Worker.Http;

            namespace FunctionsFixture;

            public static class FunctionsEndpoints
            {
                [Function("health")]
                public static async Task<HttpResponseData> Health(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData request)
                {
                    var response = request.CreateResponse(HttpStatusCode.OK);
                    var built = typeof(FunctionsEndpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                        .Single(attribute => attribute.Key == "BuildFlavor").Value;
                    await response.WriteStringAsync($"{built}|{Environment.GetEnvironmentVariable("BUILD_FLAVOR")}").ConfigureAwait(false);
                    return response;
                }

                [Function("enqueue")]
                [QueueOutput("roundtrip", Connection = "queue")]
                public static async Task<string> Enqueue(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData request)
                {
                    return (await request.ReadAsStringAsync().ConfigureAwait(false))!;
                }
            }
            """);

        return projectPath;
    }

    public static async Task<string> RunContainerRuntimeAsync(
        IContainerRuntime runtime,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var executable = runtime.Name switch
        {
            "Docker" => "docker",
            "Podman" => "podman",
            _ => throw new InvalidOperationException($"Unexpected container runtime '{runtime.Name}'.")
        };
        var (pendingResult, process) = ProcessUtil.Run(new ProcessSpec(executable)
        {
            ArgumentList = arguments,
            ThrowOnNonZeroReturnCode = true
        });
        await using var processLifetime = process.ConfigureAwait(false);
        var result = await pendingResult.WaitAsync(cancellationToken);
        Assert.Equal(0, result.ExitCode);

        return string.Join(Environment.NewLine, result.ProcessOutput).Trim();
    }
}
