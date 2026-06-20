// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardFeedbackDiagnosticProviderTests
{
    [Fact]
    public void NormalizeAspireDoctorOutput_ExtractsJsonObjectFromProgressOutput()
    {
        var output = """
            {
              "checks": [
                {
                  "category": "aspire",
                  "message": "A string with } and { braces",
                  "status": "ok"
                }
              ]
            }

            Checking Aspire environment...
            """;

        var result = DashboardFeedbackDiagnosticProvider.NormalizeAspireDoctorOutput(output);

        var expected = """
            {
              "checks": [
                {
                  "category": "aspire",
                  "message": "A string with } and { braces",
                  "status": "ok"
                }
              ]
            }
            """;
        Assert.Equal(expected, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeAspireDoctorOutput_ReturnsTrimmedOutputWhenJsonIsUnavailable()
    {
        var result = DashboardFeedbackDiagnosticProvider.NormalizeAspireDoctorOutput("""
            Could not capture `aspire doctor` output.

            """);

        Assert.Equal("Could not capture `aspire doctor` output.", result);
    }

    [Fact]
    public void BuildAdditionalContext_NoAppHostFilePath_OmitsAppHostLine()
    {
        var provider = CreateProvider(new ConfigurationBuilder().Build());

        var result = provider.BuildAdditionalContext();

        Assert.Collection(GetNonEmptyLines(result),
            line => Assert.StartsWith("- Posted from: Dashboard", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Aspire version:", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Operating system:", line, StringComparison.Ordinal),
            line => Assert.Equal("- Dashboard route: /resources", line));
    }

    [Fact]
    public void BuildAdditionalContext_CSharpProjectAppHost_IncludesProjectDetails()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "MyApp.AppHost.csproj");
        File.WriteAllText(appHostPath, """
            <Project Sdk="Aspire.AppHost.Sdk/13.5.0-preview.1.26319.9">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var provider = CreateProvider(CreateConfiguration(appHostPath));

        var result = provider.BuildAdditionalContext();

        Assert.Contains($"- AppHost: C# at `{appHostPath}` using Aspire.AppHost.Sdk 13.5.0-preview.1.26319.9 targeting `net10.0`", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAdditionalContext_CSharpSingleFileAppHost_IncludesSdkDirective()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "apphost.cs");
        File.WriteAllText(appHostPath, """
            #:sdk Aspire.AppHost.Sdk@13.5.0

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);
        var provider = CreateProvider(CreateConfiguration(appHostPath));

        var result = provider.BuildAdditionalContext();

        Assert.Contains($"- AppHost: C# at `{appHostPath}` using Aspire.AppHost.Sdk 13.5.0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAdditionalContext_TypeScriptAppHost_IncludesLanguageAndNodeEngine()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "apphost.mts");
        File.WriteAllText(appHostPath, "await createBuilder();");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "package.json"), """
            {
              "engines": {
                "node": ">=24.15.0"
              }
            }
            """);
        var provider = CreateProvider(CreateConfiguration(appHostPath));

        var result = provider.BuildAdditionalContext();

        Assert.Contains($"- AppHost: TypeScript at `{appHostPath}` on Node.js >=24.15.0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAdditionalContext_ConfiguredMissingAppHostFile_OmitsAppHostLine()
    {
        using var tempDirectory = new TemporaryDirectory();
        var provider = CreateProvider(CreateConfiguration(Path.Combine(tempDirectory.Path, "missing.csproj")));

        var result = provider.BuildAdditionalContext();

        Assert.Collection(GetNonEmptyLines(result),
            line => Assert.StartsWith("- Posted from: Dashboard", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Aspire version:", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Operating system:", line, StringComparison.Ordinal),
            line => Assert.Equal("- Dashboard route: /resources", line));
    }

    private static string[] GetNonEmptyLines(string value)
    {
        return value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static DashboardFeedbackDiagnosticProvider CreateProvider(IConfiguration configuration)
    {
        return new DashboardFeedbackDiagnosticProvider(new TestNavigationManager(), configuration, NullLogger<DashboardFeedbackDiagnosticProvider>.Instance);
    }

    private static IConfiguration CreateConfiguration(string appHostPath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>(DashboardConfigNames.AppHostFilePathName.ConfigKey, appHostPath)
            ])
            .Build();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/resources");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("dashboard-feedback-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
