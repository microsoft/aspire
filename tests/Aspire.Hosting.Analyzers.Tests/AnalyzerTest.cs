// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Aspire.Hosting.Analyzers.Tests;

internal static class AnalyzerTest
{
    public static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> Create<TAnalyzer>(string source, IEnumerable<DiagnosticResult> expectedDiagnostics)
        where TAnalyzer : DiagnosticAnalyzer, new()
        => Create<TAnalyzer>(source, expectedDiagnostics, includeAspireHostingReference: true);

    public static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> Create<TAnalyzer>(string source, IEnumerable<DiagnosticResult> expectedDiagnostics, bool includeAspireHostingReference, string? isAspirePolyglotCompatible = "true")
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { source },
                // This is required to allow the use of top-level statements in the test source.
                OutputKind = Microsoft.CodeAnalysis.OutputKind.ConsoleApplication
            },
            ReferenceAssemblies = GetReferenceAssemblies(includeAspireHostingReference)
        };

        // Surface the <IsAspirePolyglotCompatible> build property to the analyzer the same way MSBuild
        // does (via a CompilerVisibleProperty -> build_property.* global analyzer config option). Tests
        // default to "true" so existing [AspireExport] cases don't trip ASPIREEXPORT017; pass null to omit
        // the marker entirely (simulating an integration that forgot to opt in).
        if (isAspirePolyglotCompatible is not null)
        {
            test.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", $"""
                is_global = true
                build_property.IsAspirePolyglotCompatible = {isAspirePolyglotCompatible}

                """));
        }

        test.ExpectedDiagnostics.AddRange(expectedDiagnostics);
        return test;
    }

    private static string s_targetFrameworkVersion => typeof(ResourceNameAnalyzerTests).Assembly
        .GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName[".NETCoreApp,Version=v".Length..];

    private static ReferenceAssemblies GetReferenceAssemblies(bool includeAspireHostingReference)
    {
        var netCoreAppRef = new ReferenceAssemblies(
            $"net{s_targetFrameworkVersion}",
            new PackageIdentity("Microsoft.NETCore.App.Ref", $"{s_targetFrameworkVersion}.0"),
            Path.Combine("ref", $"net{s_targetFrameworkVersion}"));

        if (!includeAspireHostingReference)
        {
            return netCoreAppRef;
        }

        return netCoreAppRef.AddAssemblies([TrimAssemblyExtension(typeof(DistributedApplication).Assembly.Location)]);
    }

    private static string TrimAssemblyExtension(string fullPath) => fullPath.Replace(".dll", string.Empty);
}
