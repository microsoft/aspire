// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Hosting.RemoteHost.Language;
using Aspire.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class CodeGenerationResolverTests
{
    [Fact]
    public void CodeGeneratorResolver_DiscoversInternalCodeGenerators()
    {
        using var serviceProvider = CreateServiceProvider();
        var assemblyLoader = CreateAssemblyLoader();
        var resolver = new CodeGeneratorResolver(serviceProvider, assemblyLoader, NullLogger<CodeGeneratorResolver>.Instance);

        Assert.NotNull(resolver.GetCodeGenerator("Go"));
        Assert.NotNull(resolver.GetCodeGenerator("Java"));
        Assert.NotNull(resolver.GetCodeGenerator("Python"));
        Assert.NotNull(resolver.GetCodeGenerator("Rust"));
        Assert.NotNull(resolver.GetCodeGenerator("TypeScript"));
    }

    /// <summary>
    /// API export is discovered as its own type, and the code generator itself must not implement
    /// the exporter contract.
    /// </summary>
    /// <remarks>
    /// <c>Aspire.TypeSystem</c> is force-shared from the apphost server's default
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> and freezes its strong-name
    /// <c>AssemblyVersion</c> at a constant, so a CLI that predates
    /// <see cref="IApiReferenceExporter"/> still binds a newer SDK's codegen assembly — it just has
    /// no such interface in its bundled copy. A type's interface list is resolved eagerly when the
    /// type loads, so a generator implementing the interface would itself fail to load there and
    /// TypeScript generation, not just export, would disappear. Keeping export on a separate type
    /// confines the loss to the feature that CLI could not use anyway.
    /// </remarks>
    [Fact]
    public void CodeGeneratorResolver_ResolvesApiReferenceExporterWithoutItLivingOnTheCodeGenerator()
    {
        using var serviceProvider = CreateServiceProvider();
        var assemblyLoader = CreateAssemblyLoader();
        var resolver = new CodeGeneratorResolver(serviceProvider, assemblyLoader, NullLogger<CodeGeneratorResolver>.Instance);

        var generator = resolver.GetCodeGenerator("TypeScript");
        Assert.NotNull(generator);
        Assert.IsNotAssignableFrom<IApiReferenceExporter>(generator);

        var exporter = resolver.GetApiReferenceExporter("TypeScript");
        Assert.NotNull(exporter);
        Assert.Equal("TypeScript", exporter.Language);
    }

    /// <summary>
    /// A documented API that no generator produces would be worse than no documentation at all, so
    /// discovering exporters independently must not make one reachable for an unsupported language.
    /// </summary>
    [Fact]
    public void CodeGeneratorResolver_DoesNotResolveAnExporterForALanguageWithNoGenerator()
    {
        using var serviceProvider = CreateServiceProvider();
        var assemblyLoader = CreateAssemblyLoader();
        var resolver = new CodeGeneratorResolver(serviceProvider, assemblyLoader, NullLogger<CodeGeneratorResolver>.Instance);

        Assert.Null(resolver.GetCodeGenerator("Klingon"));
        Assert.Null(resolver.GetApiReferenceExporter("Klingon"));
    }

    [Fact]
    public void LanguageSupportResolver_DiscoversInternalLanguageSupports()
    {
        using var serviceProvider = CreateServiceProvider();
        var assemblyLoader = CreateAssemblyLoader();
        var resolver = new LanguageSupportResolver(serviceProvider, assemblyLoader, NullLogger<LanguageSupportResolver>.Instance);

        Assert.NotNull(resolver.GetLanguageSupport("go"));
        Assert.NotNull(resolver.GetLanguageSupport("java"));
        Assert.NotNull(resolver.GetLanguageSupport("python"));
        Assert.NotNull(resolver.GetLanguageSupport("rust"));
        Assert.NotNull(resolver.GetLanguageSupport("typescript/nodejs"));
    }

    private static ServiceProvider CreateServiceProvider() => new ServiceCollection().BuildServiceProvider();

    private static AssemblyLoader CreateAssemblyLoader()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AtsAssemblies:0"] = "Aspire.Hosting.CodeGeneration.Go",
                ["AtsAssemblies:1"] = "Aspire.Hosting.CodeGeneration.Java",
                ["AtsAssemblies:2"] = "Aspire.Hosting.CodeGeneration.Python",
                ["AtsAssemblies:3"] = "Aspire.Hosting.CodeGeneration.Rust",
                ["AtsAssemblies:4"] = "Aspire.Hosting.CodeGeneration.TypeScript",
            })
            .Build();

        return new AssemblyLoader(configuration, NullLogger<AssemblyLoader>.Instance, new RemoteHostProfilingTelemetry(new ConfigurationBuilder().Build()));
    }
}
