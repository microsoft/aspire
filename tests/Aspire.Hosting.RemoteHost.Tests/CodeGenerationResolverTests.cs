// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Language;
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

        return new AssemblyLoader(configuration, NullLogger<AssemblyLoader>.Instance);
    }
}
