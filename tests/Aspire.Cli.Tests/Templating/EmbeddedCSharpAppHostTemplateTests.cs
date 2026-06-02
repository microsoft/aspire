// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Integration tests for <see cref="EmbeddedCSharpAppHostTemplate"/>. These
/// render the actual embedded template tree into a temp directory and assert
/// on the resulting file layout / content. They are the cross-check that the
/// .NET-template-engine replacement produces output equivalent (in shape) to
/// what <c>dotnet new aspire-apphost</c> previously emitted.
/// </summary>
public class EmbeddedCSharpAppHostTemplateTests
{
    [Fact]
    public async Task RenderAsync_WritesExpectedFileTree()
    {
        using var output = new TempDir();

        await EmbeddedCSharpAppHostTemplate.RenderAsync(
            output.Path,
            projectName: "MyApp.AppHost",
            useLocalhostTld: false,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        // The csproj filename and aspire.config.json contents both depend on the
        // path/content transformers respectively — covering them together gives
        // us coverage of both passes from a single integration test.
        Assert.True(File.Exists(Path.Combine(output.Path, "MyApp.AppHost.csproj")));
        Assert.True(File.Exists(Path.Combine(output.Path, "AppHost.cs")));
        Assert.True(File.Exists(Path.Combine(output.Path, "appsettings.json")));
        Assert.True(File.Exists(Path.Combine(output.Path, "appsettings.Development.json")));
        Assert.True(File.Exists(Path.Combine(output.Path, "aspire.config.json")));
        Assert.True(File.Exists(Path.Combine(output.Path, "Properties", "launchSettings.json")));
    }

    [Fact]
    public async Task RenderAsync_CsprojContainsProjectNameAndAspireVersion()
    {
        using var output = new TempDir();

        await EmbeddedCSharpAppHostTemplate.RenderAsync(
            output.Path,
            projectName: "MyApp.AppHost",
            useLocalhostTld: false,
            templateVersion: "13.4.0-test",
            NullLogger.Instance,
            CancellationToken.None);

        var csproj = File.ReadAllText(Path.Combine(output.Path, "MyApp.AppHost.csproj"));
        Assert.Contains("13.4.0-test", csproj);
        // No unresolved tokens should be left behind.
        Assert.DoesNotContain("{{aspireVersion}}", csproj);
        Assert.DoesNotContain("{{projectName}}", csproj);
        Assert.DoesNotContain("{{userSecretsId}}", csproj);
    }

    [Fact]
    public async Task RenderAsync_AspireConfigPointsAtCsproj()
    {
        using var output = new TempDir();

        await EmbeddedCSharpAppHostTemplate.RenderAsync(
            output.Path,
            projectName: "MyApp.AppHost",
            useLocalhostTld: false,
            templateVersion: null,
            NullLogger.Instance,
            CancellationToken.None);

        var configJson = File.ReadAllText(Path.Combine(output.Path, "aspire.config.json"));
        // The CLI reads this JSON later via `aspire run` / `aspire add`. Validating
        // shape (parsable, expected pointer) here means a future templating change
        // can't silently break the AppHost discovery path.
        using var doc = JsonDocument.Parse(configJson);
        var path = doc.RootElement.GetProperty("appHost").GetProperty("path").GetString();
        Assert.Equal("MyApp.AppHost.csproj", path);
    }

    [Fact]
    public async Task RenderAsync_WithLocalhostTld_KeepsTldUrl()
    {
        using var output = new TempDir();

        await EmbeddedCSharpAppHostTemplate.RenderAsync(
            output.Path,
            projectName: "My-App",
            useLocalhostTld: true,
            templateVersion: null,
            NullLogger.Instance,
            CancellationToken.None);

        var launchSettings = File.ReadAllText(Path.Combine(output.Path, "Properties", "launchSettings.json"));
        // Marker lines from both positive and inverted blocks must be gone.
        Assert.DoesNotContain("{{#localhostTld}}", launchSettings);
        Assert.DoesNotContain("{{^localhostTld}}", launchSettings);
        Assert.DoesNotContain("{{/localhostTld}}", launchSettings);
        // The hostname is computed via the lowerCaseInvariantWithHyphens chain.
        Assert.Contains("my-app.dev.localhost", launchSettings);
    }

    [Fact]
    public async Task RenderAsync_WithoutLocalhostTld_UsesPlainLocalhost()
    {
        using var output = new TempDir();

        await EmbeddedCSharpAppHostTemplate.RenderAsync(
            output.Path,
            projectName: "My-App",
            useLocalhostTld: false,
            templateVersion: null,
            NullLogger.Instance,
            CancellationToken.None);

        var launchSettings = File.ReadAllText(Path.Combine(output.Path, "Properties", "launchSettings.json"));
        Assert.DoesNotContain("{{#localhostTld}}", launchSettings);
        Assert.DoesNotContain("{{^localhostTld}}", launchSettings);
        Assert.DoesNotContain("{{/localhostTld}}", launchSettings);
        Assert.DoesNotContain("my-app.dev.localhost", launchSettings);
        Assert.Contains("localhost", launchSettings);
    }

    [Fact]
    public async Task RenderAsync_GeneratesUniqueUserSecretsId()
    {
        // Each scaffold should get its own UserSecretsId so two `aspire init` runs
        // don't accidentally share a secrets store on the developer's machine.
        using var outputA = new TempDir();
        using var outputB = new TempDir();

        await EmbeddedCSharpAppHostTemplate.RenderAsync(
            outputA.Path, "AppA", false, null, NullLogger.Instance, CancellationToken.None);
        await EmbeddedCSharpAppHostTemplate.RenderAsync(
            outputB.Path, "AppB", false, null, NullLogger.Instance, CancellationToken.None);

        var csprojA = File.ReadAllText(Path.Combine(outputA.Path, "AppA.csproj"));
        var csprojB = File.ReadAllText(Path.Combine(outputB.Path, "AppB.csproj"));

        var idA = ExtractUserSecretsId(csprojA);
        var idB = ExtractUserSecretsId(csprojB);

        Assert.NotEqual(Guid.Empty, idA);
        Assert.NotEqual(Guid.Empty, idB);
        Assert.NotEqual(idA, idB);
    }

    [Theory]
    [InlineData("MyApp", "myapp")]
    [InlineData("My App", "my-app")]
    [InlineData("My  App", "my-app")]
    [InlineData("My.App.Tests", "my-app-tests")]
    [InlineData("--leading-hyphens", "leading-hyphens")]
    [InlineData("MIXED_Case.123", "mixed-case-123")]
    public void ComputeLocalhostTldHostName_ProducesDnsSafeForm(string input, string expected)
    {
        // Locks in parity with the standalone aspire-apphost template's
        // `lowerCaseInvariantWithHyphens` form chain so the embedded path emits the
        // same launchSettings URL the .NET template engine would have produced.
        var result = EmbeddedCSharpAppHostTemplate.ComputeLocalhostTldHostName(input);
        Assert.Equal(expected, result);
    }

    private static Guid ExtractUserSecretsId(string csproj)
    {
        const string open = "<UserSecretsId>";
        const string close = "</UserSecretsId>";
        var start = csproj.IndexOf(open, StringComparison.Ordinal);
        var end = csproj.IndexOf(close, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "csproj did not contain a UserSecretsId element");
        var value = csproj.Substring(start + open.Length, end - start - open.Length);
        return Guid.Parse(value);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("aspire-cli-csharp-apphost-tests-");
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
