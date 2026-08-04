// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

/// <summary>
/// Coverage for the diagnostics surfaced by the discovery resolvers when type loading fails or
/// when a "code generation" assembly is loaded but contributes no implementations. These tests
/// guard the user-facing fix for https://github.com/microsoft/aspire/issues/16729 — a previously
/// silent ReflectionTypeLoadException that produced a downstream "no code generator found" /
/// "no language support found" error with no actionable diagnostic.
/// </summary>
public class ResolverDiagnosticsTests
{
    [Fact]
    public void CodeGeneratorResolver_LogsWarning_WhenAssemblyTypeLoadFails()
    {
        var logger = new RecordingLogger<CodeGeneratorResolver>();
        var stub = new TypeLoadFailingAssembly("Aspire.Hosting.CodeGeneration.Synthetic");

        using var services = new ServiceCollection().BuildServiceProvider();
        var resolver = new CodeGeneratorResolver(services, () => (IReadOnlyList<Assembly>)[stub], logger);

        Assert.Null(resolver.GetCodeGenerator("anything"));

        var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be loaded"));
        Assert.NotNull(warning);
        Assert.Contains("LoaderExceptions", warning!.Message);
        Assert.Contains(stub.GetName().Name!, warning.Message);
        Assert.Contains("synthetic loader exception", warning.Message);
    }

    [Fact]
    public void LanguageSupportResolver_LogsWarning_WhenAssemblyTypeLoadFails()
    {
        var logger = new RecordingLogger<LanguageSupportResolver>();
        var stub = new TypeLoadFailingAssembly("Aspire.Hosting.CodeGeneration.Synthetic");

        using var services = new ServiceCollection().BuildServiceProvider();
        var resolver = new LanguageSupportResolver(services, () => (IReadOnlyList<Assembly>)[stub], logger);

        Assert.Null(resolver.GetLanguageSupport("anything"));

        var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be loaded"));
        Assert.NotNull(warning);
        Assert.Contains("LoaderExceptions", warning!.Message);
        Assert.Contains(stub.GetName().Name!, warning.Message);
        Assert.Contains("synthetic loader exception", warning.Message);
    }

    [Fact]
    public void CodeGeneratorResolver_LogsWarning_WhenCodeGenerationAssemblyContributesNothing()
    {
        var logger = new RecordingLogger<CodeGeneratorResolver>();
        // The marker name is what triggers the "did not contribute any" diagnostic.
        var empty = new EmptyNamedAssembly("Aspire.Hosting.CodeGeneration.Synthetic");

        using var services = new ServiceCollection().BuildServiceProvider();
        var resolver = new CodeGeneratorResolver(services, () => (IReadOnlyList<Assembly>)[empty], logger);

        Assert.Null(resolver.GetCodeGenerator("anything"));

        var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning && e.Message.Contains("did not contribute"));
        Assert.NotNull(warning);
        Assert.Contains("ICodeGenerator", warning!.Message);
    }

    [Fact]
    public void CodeGeneratorResolver_DoesNotLogContributionWarning_ForArbitraryAssembly()
    {
        var logger = new RecordingLogger<CodeGeneratorResolver>();
        var arbitrary = new EmptyNamedAssembly("My.Custom.Integration");

        using var services = new ServiceCollection().BuildServiceProvider();
        _ = new CodeGeneratorResolver(services, () => (IReadOnlyList<Assembly>)[arbitrary], logger).GetCodeGenerator("anything");

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not contribute"));
    }

    private sealed class TypeLoadFailingAssembly : Assembly
    {
        private readonly AssemblyName _name;

        public TypeLoadFailingAssembly(string name) => _name = new AssemblyName(name);

        public override AssemblyName GetName() => _name;

        public override Type[] GetTypes()
            => throw new ReflectionTypeLoadException(
                [null, typeof(string)],
                [new FileLoadException("synthetic loader exception: simulated Aspire.TypeSystem mismatch")]);
    }

    private sealed class EmptyNamedAssembly : Assembly
    {
        private readonly AssemblyName _name;

        public EmptyNamedAssembly(string name) => _name = new AssemblyName(name);

        public override AssemblyName GetName() => _name;

        public override Type[] GetTypes() => [typeof(string)];
    }
}
