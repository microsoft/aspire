// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

/// <summary>
/// Verifies the user-facing error messages on the RPC-exposed services include actionable
/// information instead of just "No language support found for: X". See
/// https://github.com/microsoft/aspire/issues/16729 for the background.
/// </summary>
public class ServiceErrorMessageTests
{
    [Fact]
    public void ScaffoldAppHost_UnknownLanguage_ListsAvailableLanguages()
    {
        var (langService, _) = CreateServices();

        var ex = Assert.Throws<ArgumentException>(() => langService.ScaffoldAppHost("klingon", "/tmp/whatever"));

        Assert.Contains("No language support found for: klingon", ex.Message);
        Assert.Contains("Available languages:", ex.Message);
        Assert.Contains("typescript/nodejs", ex.Message);
    }

    [Fact]
    public void GenerateCode_UnknownLanguage_ListsAvailableLanguages()
    {
        var (_, codeService) = CreateServices();

        var ex = Assert.Throws<ArgumentException>(() => codeService.GenerateCode("klingon"));

        Assert.Contains("No code generator found for language: klingon", ex.Message);
        Assert.Contains("Available languages:", ex.Message);
        Assert.Contains("TypeScript", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScaffoldAppHost_NoLanguagesDiscovered_PointsAtBundleMismatch()
    {
        var langService = CreateLanguageServiceWithEmptyResolver();

        var ex = Assert.Throws<ArgumentException>(() => langService.ScaffoldAppHost("typescript/nodejs", "/tmp/whatever"));

        Assert.Contains("No language support found for: typescript/nodejs", ex.Message);
        Assert.Contains("LoaderExceptions", ex.Message);
        Assert.Contains("binary mismatch", ex.Message);
    }

    [Fact]
    public void GenerateCode_NoGeneratorsDiscovered_PointsAtBundleMismatch()
    {
        var codeService = CreateCodeGenerationServiceWithEmptyResolver();

        var ex = Assert.Throws<ArgumentException>(() => codeService.GenerateCode("TypeScript"));

        Assert.Contains("No code generator found for language: TypeScript", ex.Message);
        Assert.Contains("LoaderExceptions", ex.Message);
        Assert.Contains("binary mismatch", ex.Message);
    }

    private static (LanguageService Lang, CodeGenerationService Code) CreateServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AtsAssemblies:0"] = "Aspire.Hosting.CodeGeneration.Go",
                ["AtsAssemblies:1"] = "Aspire.Hosting.CodeGeneration.Java",
                ["AtsAssemblies:2"] = "Aspire.Hosting.CodeGeneration.Python",
                ["AtsAssemblies:3"] = "Aspire.Hosting.CodeGeneration.Rust",
                ["AtsAssemblies:4"] = "Aspire.Hosting.CodeGeneration.TypeScript"
            })
            .Build();

        var loader = new AssemblyLoader(configuration, NullLogger<AssemblyLoader>.Instance);
        // Note: do NOT dispose the ServiceProvider here. The resolvers lazily instantiate
        // language-support / code-generator types via ActivatorUtilities, which would fail
        // if the provider had already been disposed.
        var services = new ServiceCollection().BuildServiceProvider();

        var langResolver = new LanguageSupportResolver(services, loader, NullLogger<LanguageSupportResolver>.Instance);
        var codeResolver = new CodeGeneratorResolver(services, loader, NullLogger<CodeGeneratorResolver>.Instance);

        var auth = CreateAuthenticatedState();

        var atsContextFactory = new AtsContextFactory(loader, NullLogger<AtsContextFactory>.Instance);

        var lang = new LanguageService(auth, langResolver, NullLogger<LanguageService>.Instance);
        var code = new CodeGenerationService(auth, atsContextFactory, codeResolver, NullLogger<CodeGenerationService>.Instance);
        return (lang, code);
    }

    private static LanguageService CreateLanguageServiceWithEmptyResolver()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        // Use the test-only seam to inject an empty assembly list so the resolver hits the
        // "no implementations discovered" branch deterministically, regardless of what
        // AssemblyLoader probing finds in the test runtime directory.
        var langResolver = new LanguageSupportResolver(
            services,
            Array.Empty<Assembly>,
            NullLogger<LanguageSupportResolver>.Instance);

        var auth = CreateAuthenticatedState();
        return new LanguageService(auth, langResolver, NullLogger<LanguageService>.Instance);
    }

    private static CodeGenerationService CreateCodeGenerationServiceWithEmptyResolver()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var loader = new AssemblyLoader(configuration, NullLogger<AssemblyLoader>.Instance);
        var services = new ServiceCollection().BuildServiceProvider();
        var codeResolver = new CodeGeneratorResolver(
            services,
            Array.Empty<Assembly>,
            NullLogger<CodeGeneratorResolver>.Instance);

        var auth = CreateAuthenticatedState();
        var atsContextFactory = new AtsContextFactory(loader, NullLogger<AtsContextFactory>.Instance);
        return new CodeGenerationService(auth, atsContextFactory, codeResolver, NullLogger<CodeGenerationService>.Instance);
    }

    // The default state is "authenticated" when no JsonRpcAuthToken is present in configuration.
    private static JsonRpcAuthenticationState CreateAuthenticatedState()
        => new(new ConfigurationBuilder().Build());
}
