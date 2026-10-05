// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

/// <summary>
/// Covers the canonical API export RPC. The export is what documentation sites bind to, so the
/// contract it enforces matters as much as the payload: exports must be scoped to the requested
/// package, must fail loudly for languages that cannot produce one, and must never be reshaped by
/// RemoteHost.
/// </summary>
public class ApiReferenceExportTests
{
    [Fact]
    public void ExportApi_TypeScript_ReturnsCanonicalSchemaForRequestedPackage()
    {
        var service = CreateCodeGenerationService();

        var export = service.ExportApi("TypeScript", "Aspire.Hosting", "13.5.0", CancellationToken.None);
        var repeatedExport = service.ExportApi("TypeScript", "Aspire.Hosting", "13.5.0", CancellationToken.None);

        Assert.Equal(export.GetRawText(), repeatedExport.GetRawText());
        Assert.Equal(1, export.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("typescript", export.GetProperty("language").GetString());
        Assert.Equal("Aspire.Hosting", export.GetProperty("package").GetProperty("name").GetString());
        Assert.Equal("13.5.0", export.GetProperty("package").GetProperty("version").GetString());

        var modules = export.GetProperty("modules").EnumerateArray().ToList();
        Assert.NotEmpty(modules);

        var items = modules.SelectMany(module => module.GetProperty("items").EnumerateArray()).ToList();
        Assert.NotEmpty(items);

        // The whole point of the export is that documented declarations are final TypeScript, not
        // ATS type identifiers.
        Assert.All(items, item => Assert.DoesNotContain(
            "Aspire.Hosting/",
            item.GetProperty("declaration").GetString()!,
            StringComparison.Ordinal));

        Assert.NotEmpty(export.GetProperty("declarations").EnumerateArray());
    }

    [Fact]
    public void ExportApi_ScopesDocumentedItemsToRequestedPackage()
    {
        var service = CreateCodeGenerationService();

        var export = service.ExportApi("TypeScript", "Aspire.Hosting", "13.5.0", CancellationToken.None);

        // Referenced types reach the export through the closure so the declarations type-check, but
        // they must not be documented here: the package that owns them publishes them.
        var declarationOwners = export.GetProperty("declarations").EnumerateArray()
            .Select(declaration => declaration.GetProperty("owningAssembly").GetString())
            .ToHashSet(StringComparer.Ordinal);

        var items = export.GetProperty("modules").EnumerateArray()
            .SelectMany(module => module.GetProperty("items").EnumerateArray())
            .ToList();

        // Augmentations are the exception: they carry the members this package contributes to a type
        // another package owns, so they report that owner and use a distinct stable ID rather than
        // publishing a second page for someone else's type.
        var ownedItemOwners = items
            .Where(item => item.GetProperty("kind").GetString() != "augmentation")
            .Select(item => item.GetProperty("owningAssembly").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(ownedItemOwners, owner => Assert.Equal("Aspire.Hosting", owner));

        Assert.All(
            items.Where(item => item.GetProperty("kind").GetString() == "augmentation"),
            item =>
            {
                Assert.StartsWith("augmentation:", item.GetProperty("id").GetString(), StringComparison.Ordinal);
                Assert.NotEqual("Aspire.Hosting", item.GetProperty("owningAssembly").GetString());
            });

        Assert.Contains("Aspire.Hosting", declarationOwners);
    }

    [Fact]
    public void ExportApi_UsesGlobalPackagesPathsForRequestedPackage()
    {
        using var manifestDirectory = new TemporaryDirectory();
        var manifestPath = Path.Combine(manifestDirectory.Path, "integration-package-probe-manifest.json");
        var packageAssetsPath = Path.Combine(
            manifestDirectory.Path,
            "packages",
            "contoso.aspire.metapackage",
            "1.2.3");

        var hostingAssemblyPath = CopyPackageAssembly(
            typeof(IDistributedApplicationBuilder).Assembly.Location,
            packageAssetsPath,
            "runtimes",
            "test-rid",
            "lib",
            "net8.0");
        var yarpAssemblyPath = CopyPackageAssembly(
            typeof(Yarp.YarpResource).Assembly.Location,
            packageAssetsPath,
            "REF",
            "NET8.0");

        WriteProbeManifest(
            manifestPath,
            managedAssemblies:
            [
                new
                {
                    Name = "Aspire.Hosting",
                    Path = hostingAssemblyPath,
                    PackageId = "Contoso.Aspire.MetaPackage"
                },
                new
                {
                    Name = "Aspire.Hosting.Yarp",
                    Path = yarpAssemblyPath,
                    PackageId = "Contoso.Aspire.MetaPackage"
                }
            ]);
        var service = CreateCodeGenerationService(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_PROBE_MANIFEST_PATH"] = manifestPath
        });

        var versionMismatch = Assert.Throws<InvalidOperationException>(
            () => service.ExportApi("TypeScript", "Contoso.Aspire.MetaPackage", "9.9.9", CancellationToken.None));
        Assert.Contains("9.9.9", versionMismatch.Message, StringComparison.Ordinal);

        var export = service.ExportApi("TypeScript", "contoso.aspire.metapackage", "1.2.3", CancellationToken.None);

        Assert.Equal("Contoso.Aspire.MetaPackage", export.GetProperty("package").GetProperty("name").GetString());

        var ownedItemOwners = export.GetProperty("modules").EnumerateArray()
            .SelectMany(module => module.GetProperty("items").EnumerateArray())
            .Where(item => item.GetProperty("kind").GetString() != "augmentation")
            .Select(item => item.GetProperty("owningAssembly").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Aspire.Hosting", ownedItemOwners);
        Assert.Contains("Aspire.Hosting.Yarp", ownedItemOwners);
    }

    [Fact]
    public void ExportApi_UnknownLanguage_ListsAvailableLanguages()
    {
        var service = CreateCodeGenerationService();

        var ex = Assert.Throws<LocalRpcException>(() => service.ExportApi("klingon", "Aspire.Hosting", "13.5.0", CancellationToken.None));

        Assert.Equal((int)JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("No code generator found for language: klingon", ex.Message);
        Assert.Contains("Available languages:", ex.Message);
    }

    [Fact]
    public void ExportApi_GeneratorWithoutExporter_ReportsUnsupportedLanguage()
    {
        var service = CreateCodeGenerationService();

        // Go generates runtime source but ships no IApiReferenceExporter, so asking it for
        // an API export has to fail with a message that names the gap rather than returning an empty
        // document that a documentation site would silently publish.
        var ex = Assert.Throws<LocalRpcException>(() => service.ExportApi("Go", "Aspire.Hosting", "13.5.0", CancellationToken.None));

        Assert.Equal((int)JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("Go", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IApiReferenceExporter), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ExportApi_MissingPackageName_Throws(string? packageName)
    {
        var service = CreateCodeGenerationService();

        var ex = Assert.Throws<LocalRpcException>(() => service.ExportApi("TypeScript", packageName!, "13.5.0", CancellationToken.None));
        Assert.Equal((int)JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ExportApi_MissingPackageVersion_Throws(string? packageVersion)
    {
        var service = CreateCodeGenerationService();

        var ex = Assert.Throws<LocalRpcException>(() => service.ExportApi("TypeScript", "Aspire.Hosting", packageVersion!, CancellationToken.None));
        Assert.Equal((int)JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public void ExportApi_RequiresAuthentication()
    {
        var service = CreateCodeGenerationService(authenticated: false);

        Assert.ThrowsAny<Exception>(() => service.ExportApi("TypeScript", "Aspire.Hosting", "13.5.0", CancellationToken.None));
    }

    private static CodeGenerationService CreateCodeGenerationService(
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null,
        bool authenticated = true)
    {
        var configurationValues = new Dictionary<string, string?>
        {
            ["AtsAssemblies:0"] = "Aspire.Hosting.CodeGeneration.Go",
            ["AtsAssemblies:1"] = "Aspire.Hosting.CodeGeneration.TypeScript",
        };

        if (additionalConfiguration is not null)
        {
            foreach (var (key, value) in additionalConfiguration)
            {
                configurationValues[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var telemetry = new RemoteHostProfilingTelemetry(new ConfigurationBuilder().Build());
        var loader = new AssemblyLoader(configuration, NullLogger<AssemblyLoader>.Instance, telemetry);

        // Do not dispose: the resolver lazily instantiates generators through ActivatorUtilities.
        var services = new ServiceCollection().BuildServiceProvider();
        var resolver = new CodeGeneratorResolver(services, loader, NullLogger<CodeGeneratorResolver>.Instance);
        var atsContextFactory = new AtsContextFactory(loader, NullLogger<AtsContextFactory>.Instance, telemetry);

        return new CodeGenerationService(
            CreateAuthenticationState(authenticated),
            atsContextFactory,
            resolver,
            loader,
            NullLogger<CodeGenerationService>.Instance,
            telemetry);
    }

    // The state starts authenticated when no token is configured, so building an unauthenticated
    // service means configuring a token the test never presents.
    private static JsonRpcAuthenticationState CreateAuthenticationState(bool authenticated)
        => new(authenticated
            ? new ConfigurationBuilder().Build()
            : new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ASPIRE_REMOTE_APPHOST_TOKEN"] = "test-token" })
                .Build());

    private static void WriteProbeManifest(string manifestPath, IEnumerable<object>? managedAssemblies = null, IEnumerable<object>? nativeLibraries = null)
    {
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    ManagedAssemblies = managedAssemblies ?? [],
                    NativeLibraries = nativeLibraries ?? []
                },
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));
    }

    private static string CopyPackageAssembly(
        string assemblyPath,
        string packageAssetsPath,
        params string[] assetPathSegments)
    {
        var destinationDirectory = Path.Combine([packageAssetsPath, .. assetPathSegments]);
        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(assemblyPath));
        File.Copy(assemblyPath, destinationPath);
        return destinationPath;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory;

        public TemporaryDirectory()
        {
            _directory = Directory.CreateTempSubdirectory("aspire-remotehost-");
        }

        public string Path => _directory.FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
