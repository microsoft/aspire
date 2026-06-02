// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Integration tests for <see cref="EmbeddedCSharpStarterTemplate"/>. These
/// render the embedded multi-project starter into a temp directory and assert
/// on the resulting file layout / content. They are the cross-check that the
/// .NET-template-engine replacement produces output equivalent (in shape) to
/// what <c>dotnet new aspire-starter</c> previously emitted.
/// </summary>
public class EmbeddedCSharpStarterTemplateTests
{
    [Fact]
    public async Task RenderAsync_WritesExpectedProjectLayout()
    {
        using var output = new TempDir();

        await EmbeddedCSharpStarterTemplate.RenderAsync(
            output.Path,
            projectName: "MyStarter",
            useRedisCache: false,
            useLocalhostTld: false,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        // All four projects should render, with csproj paths reflecting the
        // {{projectName}} substitution AND the ._csproj -> .csproj rewrite.
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.AppHost", "MyStarter.AppHost.csproj")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.AppHost", "AppHost.cs")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.AppHost", "Properties", "launchSettings.json")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.ApiService", "MyStarter.ApiService.csproj")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.ApiService", "Program.cs")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.Web", "MyStarter.Web.csproj")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.Web", "Program.cs")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.Web", "WeatherApiClient.cs")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.Web", "Components", "App.razor")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.Web", "Components", "_Imports.razor")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.ServiceDefaults", "MyStarter.ServiceDefaults.csproj")));
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.ServiceDefaults", "Extensions.cs")));
        // Bootstrap binary asset must round-trip without being treated as a token-bearing file.
        Assert.True(File.Exists(Path.Combine(output.Path, "MyStarter.Web", "wwwroot", "lib", "bootstrap", "dist", "css", "bootstrap.min.css")));
        // Files we explicitly dropped from the embed must NOT come back.
        Assert.False(File.Exists(Path.Combine(output.Path, "MyStarter.sln")));
    }

    [Fact]
    public async Task RenderAsync_AppHostCsproj_ContainsAspireVersionAndProjectReferences()
    {
        using var output = new TempDir();

        await EmbeddedCSharpStarterTemplate.RenderAsync(
            output.Path,
            projectName: "MyStarter",
            useRedisCache: false,
            useLocalhostTld: false,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        var csproj = File.ReadAllText(Path.Combine(output.Path, "MyStarter.AppHost", "MyStarter.AppHost.csproj"));
        Assert.Contains("Aspire.AppHost.Sdk/13.4.0-test", csproj);
        Assert.Contains(@"..\MyStarter.ApiService\MyStarter.ApiService.csproj", csproj);
        Assert.Contains(@"..\MyStarter.Web\MyStarter.Web.csproj", csproj);
        // Tokens must not leak.
        Assert.DoesNotContain("{{", csproj);
        Assert.DoesNotContain("}}", csproj);
    }

    [Fact]
    public async Task RenderAsync_WithoutRedis_OmitsRedisReferences()
    {
        using var output = new TempDir();

        await EmbeddedCSharpStarterTemplate.RenderAsync(
            output.Path,
            projectName: "MyStarter",
            useRedisCache: false,
            useLocalhostTld: false,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        var apphostCsproj = File.ReadAllText(Path.Combine(output.Path, "MyStarter.AppHost", "MyStarter.AppHost.csproj"));
        var apphostCs = File.ReadAllText(Path.Combine(output.Path, "MyStarter.AppHost", "AppHost.cs"));
        var webCsproj = File.ReadAllText(Path.Combine(output.Path, "MyStarter.Web", "MyStarter.Web.csproj"));
        var webProgram = File.ReadAllText(Path.Combine(output.Path, "MyStarter.Web", "Program.cs"));

        Assert.DoesNotContain("Aspire.Hosting.Redis", apphostCsproj);
        Assert.DoesNotContain("AddRedis(", apphostCs);
        Assert.DoesNotContain("Aspire.StackExchange.Redis.OutputCaching", webCsproj);
        Assert.DoesNotContain("AddRedisOutputCache", webProgram);
        // The non-Redis branch wires up plain in-memory output caching instead.
        Assert.Contains("AddOutputCache()", webProgram);

        // Mustache markers must all be resolved.
        foreach (var content in new[] { apphostCsproj, apphostCs, webCsproj, webProgram })
        {
            Assert.DoesNotContain("{{#useRedisCache}}", content);
            Assert.DoesNotContain("{{^useRedisCache}}", content);
            Assert.DoesNotContain("{{/useRedisCache}}", content);
        }
    }

    [Fact]
    public async Task RenderAsync_WithRedis_WiresUpRedisReferences()
    {
        using var output = new TempDir();

        await EmbeddedCSharpStarterTemplate.RenderAsync(
            output.Path,
            projectName: "MyStarter",
            useRedisCache: true,
            useLocalhostTld: false,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        var apphostCsproj = File.ReadAllText(Path.Combine(output.Path, "MyStarter.AppHost", "MyStarter.AppHost.csproj"));
        var apphostCs = File.ReadAllText(Path.Combine(output.Path, "MyStarter.AppHost", "AppHost.cs"));
        var webCsproj = File.ReadAllText(Path.Combine(output.Path, "MyStarter.Web", "MyStarter.Web.csproj"));
        var webProgram = File.ReadAllText(Path.Combine(output.Path, "MyStarter.Web", "Program.cs"));

        Assert.Contains("Aspire.Hosting.Redis", apphostCsproj);
        Assert.Contains(@"AddRedis(""cache"")", apphostCs);
        Assert.Contains("Aspire.StackExchange.Redis.OutputCaching", webCsproj);
        Assert.Contains(@"AddRedisOutputCache(""cache"")", webProgram);
        // The plain in-memory output-cache fallback must NOT also be emitted.
        Assert.DoesNotContain("AddOutputCache()", webProgram);
    }

    [Fact]
    public async Task RenderAsync_WithLocalhostTld_RewritesLaunchSettings()
    {
        using var output = new TempDir();

        await EmbeddedCSharpStarterTemplate.RenderAsync(
            output.Path,
            projectName: "My-Starter",
            useRedisCache: false,
            useLocalhostTld: true,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        var launchSettings = File.ReadAllText(Path.Combine(output.Path, "My-Starter.AppHost", "Properties", "launchSettings.json"));
        Assert.Contains("my-starter.dev.localhost", launchSettings);
        Assert.DoesNotContain("{{#localhostTld}}", launchSettings);
        Assert.DoesNotContain("{{^localhostTld}}", launchSettings);
        Assert.DoesNotContain("{{/localhostTld}}", launchSettings);
    }

    [Fact]
    public async Task RenderAsync_NoUnresolvedTokensRemain()
    {
        using var output = new TempDir();

        await EmbeddedCSharpStarterTemplate.RenderAsync(
            output.Path,
            projectName: "MyStarter",
            useRedisCache: true,
            useLocalhostTld: true,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        // Any rendered text file with a stray "{{" or "}}" indicates a missing
        // token mapping (a token literal in upstream source we didn't translate)
        // or an unclosed Mustache block — both regressions we want to catch
        // before they reach a user.
        foreach (var file in Directory.EnumerateFiles(output.Path, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            // Visual-Studio `.http` files use `{{var}}` for client-side request
            // variables (e.g. `GET {{ApiService_HostAddress}}/...`). They look
            // like our Mustache tokens but are part of the .http file format
            // and intentionally pass through to the output verbatim.
            if (ext is ".png" or ".ico" or ".woff" or ".woff2" or ".ttf" or ".http")
            {
                continue;
            }

            // The bootstrap CSS/JS we embed under wwwroot/lib/ is third-party
            // content that legitimately contains `}}` (CSS rule closers). Skip
            // any path under wwwroot/lib/ so this assertion only polices files
            // we author.
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/wwwroot/lib/", StringComparison.Ordinal))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            // `{{` is the unambiguous Mustache-open marker; a stray one indicates
            // an unresolved token or an unclosed conditional block.
            Assert.DoesNotContain("{{", content);
        }
    }

    [Fact]
    public async Task RenderAsync_GeneratedClassNamePrefix_AppliedToNamespacesAndProjectsReference()
    {
        using var output = new TempDir();

        // A name with both a hyphen and a `.` followed by a digit — exercises
        // both halves of the upstream regex `(((?<=\.)|^)(?=\d)|\W)` → `_`.
        await EmbeddedCSharpStarterTemplate.RenderAsync(
            output.Path,
            projectName: "Aspire-StarterApplication.1",
            useRedisCache: false,
            useLocalhostTld: false,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        var apphostCs = File.ReadAllText(Path.Combine(output.Path, "Aspire-StarterApplication.1.AppHost", "AppHost.cs"));
        // Projects.<Prefix>_ApiService is how Aspire generates the AppHost reference.
        Assert.Contains("Projects.Aspire_StarterApplication._1_ApiService", apphostCs);
        Assert.Contains("Projects.Aspire_StarterApplication._1_Web", apphostCs);

        var imports = File.ReadAllText(Path.Combine(output.Path, "Aspire-StarterApplication.1.Web", "Components", "_Imports.razor"));
        Assert.Contains("Aspire_StarterApplication._1.Web", imports);
    }

    [Theory]
    [InlineData("MyApp", "MyApp")]
    [InlineData("My-App", "My_App")]
    [InlineData("My.App.1", "My.App._1")]
    [InlineData("1MyApp", "_1MyApp")]
    [InlineData("Aspire-StarterApplication.1", "Aspire_StarterApplication._1")]
    public void ComputeGeneratedClassNamePrefix_MatchesUpstreamRegex(string input, string expected)
    {
        // Locks in parity with the upstream `GeneratedClassNamePrefix` regex
        // symbol from src/Aspire.ProjectTemplates/templates/aspire-starter/.template.config/template.json.
        var actual = EmbeddedCSharpStarterTemplate.ComputeGeneratedClassNamePrefix(input);
        Assert.Equal(expected, actual);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("aspire-cli-csharp-starter-tests-");
        }

        public DirectoryInfo Directory { get; }
        public string Path => Directory.FullName;
        public void Dispose()
        {
            try
            {
                Directory.Delete(recursive: true);
            }
            catch
            {
            }
        }
    }
}
