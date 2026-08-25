// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Azure.Provisioning.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aspire.Hosting.Azure.Provisioning.Tests;

internal static class ProvisioningGeneratorTest
{
    private static readonly ImmutableArray<MetadataReference> s_references =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(static path => Path.GetFileNameWithoutExtension(path) is not (
            "Aspire.Hosting" or
            "Aspire.Hosting.Azure" or
            "Aspire.Hosting.Azure.Provisioning" or
            "Azure.Provisioning"))
        .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToImmutableArray();

    public static GeneratorTestResult Run(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpCompilation.Create(
            "ProvisioningGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            s_references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new AspireProvisioningProxyGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);

        return new(outputCompilation, driver.GetRunResult(), generatorDiagnostics);
    }
}

internal readonly record struct GeneratorTestResult(
    Compilation Compilation,
    GeneratorDriverRunResult RunResult,
    ImmutableArray<Diagnostic> GeneratorDiagnostics);
