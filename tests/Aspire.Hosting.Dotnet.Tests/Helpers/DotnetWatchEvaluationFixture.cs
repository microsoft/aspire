// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests;

/// <summary>Creates SDK-pinned evaluation inputs without restoring or building fixture projects.</summary>
internal sealed class DotnetWatchEvaluationFixture : IDisposable
{
    private readonly TemporaryWorkspace _workspace;

    private DotnetWatchEvaluationFixture(ITestOutputHelper output)
    {
        // Keep evaluator fixtures in the checkout, independently of global temp or user SDK/cache configuration.
        var directory = Directory.CreateDirectory(System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "watch-evaluation-tests", Guid.NewGuid().ToString("N")));
        _workspace = new TemporaryWorkspace(output, directory);
        Write("Directory.Build.props", "<Project />");
        Write("Directory.Build.targets", "<Project />");
        Write("Directory.Packages.props", "<Project />");
    }

    internal string Path => _workspace.Path;
    internal string SdkDirectory { get; private set; } = string.Empty;
    internal string Muxer { get; private set; } = string.Empty;
    internal string SdkVersion { get; private set; } = string.Empty;
    internal string[] SdkRoots { get; private set; } = [];

    internal static async Task<DotnetWatchEvaluationFixture> CreateAsync(ITestOutputHelper output)
    {
        var fixture = new DotnetWatchEvaluationFixture(output);
        try
        {
            var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator)
                .Select(directory => System.IO.Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .Concat([Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? string.Empty, Environment.ProcessPath ?? string.Empty])
                .Where(File.Exists)
                .Select(DotnetWatchProjectGraph.Normalize)
                .Select(path => System.IO.Path.GetDirectoryName(path)!)
                .Where(directory => Directory.Exists(System.IO.Path.Combine(directory, "sdk")))
                .Distinct(DotnetWatchProjectGraph.PathComparer)
                .ToArray();
            fixture.SdkRoots = candidates;
            fixture.PinSdk(fixture.Path, $"{DotnetWatchToolResolver.SupportedSdkMajor}.0.100", "latestFeature");
            var sdk = await DotnetWatchToolResolver.ResolveSdkAsync(fixture.Path, TestContext.Current.CancellationToken).ConfigureAwait(false);
            fixture.SdkDirectory = DotnetWatchProjectGraph.Normalize(sdk.SdkDirectory);
            fixture.Muxer = DotnetWatchProjectGraph.Normalize(sdk.Muxer);
            fixture.SdkVersion = sdk.SdkVersion;
            fixture.PinSdk(fixture.Path, sdk.SdkVersion);
            output.WriteLine($"Watch evaluator fixture: SDK {sdk.SdkVersion}, muxer {sdk.Muxer}.");

            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    internal string Write(string relativePath, string content)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return DotnetWatchProjectGraph.Normalize(path);
    }

    internal string Project(string name, string content = "", string sdk = "Microsoft.NET.Sdk") =>
        Write(System.IO.Path.Combine(name, name + ".csproj"), $$"""
            <Project Sdk="{{sdk}}">
              <PropertyGroup>
                <TargetFramework>net{{DotnetWatchToolResolver.SupportedSdkMajor}}.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              {{content}}
            </Project>
            """);

    internal void PinSdk(string directory, string version, string rollForward = "disable")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            System.IO.Path.Combine(directory, "global.json"),
            JsonSerializer.Serialize(new { sdk = new { version, rollForward, paths = SdkRoots.Append("$host$").ToArray() } }));
    }

    internal Task<DotnetWatchProjectGraphResult> EvaluateAsync(
        string project,
        string? configuration = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool globalProperties = true,
        string? workingDirectory = null)
    {
        var path = System.IO.Path.GetFullPath(project, Path);

        return DotnetWatchProjectGraph.EvaluateGraphAsync(
            path,
            configuration,
            environment ?? new Dictionary<string, string>(),
            globalProperties,
            workingDirectory ?? System.IO.Path.GetDirectoryName(path)!,
            TestContext.Current.CancellationToken);
    }

    internal async Task<IReadOnlyDictionary<string, string>> QueryPropertiesAsync(
        string project,
        IReadOnlyCollection<string> names,
        string configuration,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(Muxer)
        {
            WorkingDirectory = System.IO.Path.GetDirectoryName(project)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // -getProperty with no target is evaluation-only. The SDK CLI supplies normal build/run file-app
        // generation, providing an independent check against the adapter's public SDK factory.
        foreach (var argument in new[]
        {
            "build", project, "--no-restore", "-nologo", "-v:q",
            $"-getProperty:{string.Join(',', names)}", $"-property:Configuration={configuration}"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        var diagnostics = await error.ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, diagnostics);
        using var document = JsonDocument.Parse(await output.ConfigureAwait(false));

        return document.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() => _workspace.Dispose();
}
