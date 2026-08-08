// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Npm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Npm;

public class NpmRegistryResolverTests : IDisposable
{
    private const string PackageName = "@microsoft/aspire-cli";
    private const string PublicRegistry = "https://registry.npmjs.org/";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aspire-npmrc-tests");

    [Fact]
    public void Resolve_FallsBackToPublicNpmWhenNothingIsConfigured()
    {
        var resolution = CreateResolver().Resolve(PackageName);

        Assert.Equal(PublicRegistry, resolution.RegistryUri.AbsoluteUri);
        Assert.Equal("the npm built-in default", resolution.Source);
    }

    [Fact]
    public void Resolve_UsesUserNpmrcRegistry()
    {
        WriteHomeNpmrc("registry=https://npm.contoso.example/artifactory/api/npm/npm/");

        var resolution = CreateResolver().Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/artifactory/api/npm/npm/", resolution.RegistryUri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_AppendsTrailingSlashSoFeedPathsSurviveComposition()
    {
        // "https://.../npm/registry" without the trailing slash would compose to ".../npm/<package>".
        WriteHomeNpmrc("registry=https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry");

        var resolution = CreateResolver().Resolve(PackageName);

        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/%40microsoft%2Faspire-cli",
            new Uri(resolution.RegistryUri, Uri.EscapeDataString(PackageName)).AbsoluteUri);
    }

    [Fact]
    public void Resolve_ScopedRegistryOutranksGlobalRegistry()
    {
        WriteHomeNpmrc(
            "registry=https://npm.contoso.example/general/",
            "@microsoft:registry=https://npm.contoso.example/microsoft/");

        var resolution = CreateResolver().Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/microsoft/", resolution.RegistryUri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_ScopedRegistryForAnotherScopeIsIgnored()
    {
        WriteHomeNpmrc(
            "registry=https://npm.contoso.example/general/",
            "@contoso:registry=https://npm.contoso.example/contoso/");

        var resolution = CreateResolver().Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/general/", resolution.RegistryUri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_EnvironmentVariableOutranksNpmrcFiles()
    {
        WriteHomeNpmrc("registry=https://npm.contoso.example/from-file/");

        var resolution = CreateResolver(
            environment: new Dictionary<string, string>
            {
                ["NPM_CONFIG_REGISTRY"] = "https://npm.contoso.example/from-env/"
            }).Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/from-env/", resolution.RegistryUri.AbsoluteUri);
        Assert.Equal("the NPM_CONFIG_REGISTRY environment variable", resolution.Source);
    }

    [Fact]
    public void Resolve_ProjectNpmrcOutranksUserNpmrc()
    {
        WriteHomeNpmrc("registry=https://npm.contoso.example/user/");

        // npm's local prefix is the nearest ancestor holding a package.json, and that directory's
        // .npmrc is the project layer - even for a -g install.
        var project = CreateWorkingDirectory("repo", "src");
        File.WriteAllText(Path.Combine(_root.FullName, "repo", "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root.FullName, "repo", ".npmrc"), "registry=https://npm.contoso.example/project/");

        var resolution = CreateResolver(workingDirectory: project).Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/project/", resolution.RegistryUri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_IgnoresNpmrcBelowTheLocalPrefix()
    {
        // npm reads the .npmrc at the local prefix, not one sitting in an unrelated child directory.
        WriteHomeNpmrc("registry=https://npm.contoso.example/user/");

        var project = CreateWorkingDirectory("repo", "src");
        File.WriteAllText(Path.Combine(_root.FullName, "repo", "package.json"), "{}");
        File.WriteAllText(Path.Combine(project.FullName, ".npmrc"), "registry=https://npm.contoso.example/nested/");

        var resolution = CreateResolver(workingDirectory: project).Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/user/", resolution.RegistryUri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_HonorsUserConfigRedirect()
    {
        var relocated = Path.Combine(_root.FullName, "relocated-npmrc");
        File.WriteAllText(relocated, "registry=https://npm.contoso.example/relocated/");
        WriteHomeNpmrc("registry=https://npm.contoso.example/home/");

        var resolution = CreateResolver(
            environment: new Dictionary<string, string>
            {
                ["npm_config_userconfig"] = relocated
            }).Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/relocated/", resolution.RegistryUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("registry = \"https://npm.contoso.example/quoted/\"", "https://npm.contoso.example/quoted/")]
    [InlineData("registry='https://npm.contoso.example/quoted/'", "https://npm.contoso.example/quoted/")]
    [InlineData("  registry\t=\thttps://npm.contoso.example/spaced/  ", "https://npm.contoso.example/spaced/")]
    [InlineData("REGISTRY=https://npm.contoso.example/upper/", "https://npm.contoso.example/upper/")]
    public void Resolve_ParsesNpmrcValueForms(string line, string expected)
    {
        WriteHomeNpmrc(line);

        Assert.Equal(expected, CreateResolver().Resolve(PackageName).RegistryUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("; registry=https://npm.contoso.example/comment/")]
    [InlineData("# registry=https://npm.contoso.example/comment/")]
    [InlineData("[section]")]
    [InlineData("registry")]
    [InlineData("registry=")]
    [InlineData("=https://npm.contoso.example/")]
    [InlineData("registry=file:///tmp/local")]
    [InlineData("registry=not-a-url")]
    public void Resolve_IgnoresUnusableNpmrcLines(string line)
    {
        WriteHomeNpmrc(line);

        Assert.Equal(PublicRegistry, CreateResolver().Resolve(PackageName).RegistryUri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_ExpandsEnvironmentReferences()
    {
        WriteHomeNpmrc("registry=https://${NPM_HOST}/feed/");

        var resolution = CreateResolver(
            environment: new Dictionary<string, string>
            {
                ["NPM_HOST"] = "npm.contoso.example"
            }).Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/feed/", resolution.RegistryUri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_IgnoresEntryWithUndefinedEnvironmentReference()
    {
        WriteHomeNpmrc("registry=https://${NPM_HOST_NOT_SET}/feed/");

        Assert.Equal(PublicRegistry, CreateResolver().Resolve(PackageName).RegistryUri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_DoesNotReadCredentialEntries()
    {
        // The lookup is anonymous. Auth material in a .npmrc must never be materialized, so the
        // only observable effect of these lines is the registry itself.
        WriteHomeNpmrc(
            "registry=https://npm.contoso.example/feed/",
            "//npm.contoso.example/feed/:_authToken=super-secret-token",
            "_auth=BASE64CREDENTIAL",
            "email=someone@contoso.example");

        var resolution = CreateResolver().Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/feed/", resolution.RegistryUri.AbsoluteUri);
        Assert.Equal("https://npm.contoso.example/feed/", resolution.DisplayUri);
    }

    [Fact]
    public void Resolve_RedactsCredentialsEmbeddedInTheRegistryValue()
    {
        WriteHomeNpmrc("registry=https://user:super-secret-token@npm.contoso.example/feed/");

        var resolution = CreateResolver().Resolve(PackageName);

        Assert.Equal("https://npm.contoso.example/feed/", resolution.DisplayUri);
        Assert.Equal("user:super-secret-token", resolution.RegistryUri.UserInfo);
    }

    [Fact]
    public void Resolve_UnscopedPackageUsesGlobalRegistry()
    {
        WriteHomeNpmrc(
            "registry=https://npm.contoso.example/general/",
            "@microsoft:registry=https://npm.contoso.example/microsoft/");

        Assert.Equal(
            "https://npm.contoso.example/general/",
            CreateResolver().Resolve("playwright").RegistryUri.AbsoluteUri);
    }

    private NpmRegistryResolver CreateResolver(
        DirectoryInfo? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        return new NpmRegistryResolver(
            workingDirectory ?? CreateWorkingDirectory("work"),
            new DirectoryInfo(Path.Combine(_root.FullName, "home")),
            new Dictionary<string, string>(
                environment ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase),
            NullLogger<NpmRegistryResolver>.Instance);
    }

    private DirectoryInfo CreateWorkingDirectory(params string[] segments)
    {
        var path = Path.Combine([_root.FullName, .. segments]);
        return Directory.CreateDirectory(path);
    }

    private void WriteHomeNpmrc(params string[] lines)
    {
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home"));
        File.WriteAllLines(Path.Combine(home.FullName, ".npmrc"), lines);
    }

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
