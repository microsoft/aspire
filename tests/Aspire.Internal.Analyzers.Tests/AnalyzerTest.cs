// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Aspire.Internal.Analyzers.Tests;

internal static class AnalyzerTest
{
    public static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> Create<TAnalyzer>(string source, IEnumerable<DiagnosticResult> expectedDiagnostics)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { source },
                OutputKind = OutputKind.DynamicallyLinkedLibrary,
            },
            ReferenceAssemblies = GetReferenceAssemblies(),
        };
        test.ExpectedDiagnostics.AddRange(expectedDiagnostics);
        return test;
    }

    private static string s_targetFrameworkVersion => typeof(AnalyzerTest).Assembly
        .GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName[".NETCoreApp,Version=v".Length..];

    private static ReferenceAssemblies GetReferenceAssemblies()
    {
        return new ReferenceAssemblies(
            $"net{s_targetFrameworkVersion}",
            new PackageIdentity("Microsoft.NETCore.App.Ref", $"{s_targetFrameworkVersion}.0"),
            Path.Combine("ref", $"net{s_targetFrameworkVersion}"));
    }
}
