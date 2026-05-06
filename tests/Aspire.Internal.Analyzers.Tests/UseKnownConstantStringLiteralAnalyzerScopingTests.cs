// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Aspire.Internal.Analyzers.Tests;

public class UseKnownConstantStringLiteralAnalyzerScopingTests
{
    // The analyzer matches the InternalKnownConstantsAttribute by simple name, not by
    // symbol identity, so tests can declare a local stub. This mirrors what consumer
    // projects do via the <Compile Include> in src/Directory.Build.targets.
    private const string AttributeStub = """
        namespace Aspire
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            internal sealed class InternalKnownConstantsAttribute : System.Attribute
            {
                public string[]? Assemblies { get; init; }
                public string[]? Namespaces { get; init; }
            }
        }
        """;

    [Fact]
    public async Task AttributeOptIn_NonKnownNamedClass_IsDiscovered()
    {
        // A static class whose name doesn't start with "Known" is normally skipped,
        // but [InternalKnownConstants] explicitly opts it in.
        var source = AttributeStub + """

            namespace TestNs
            {
                [Aspire.InternalKnownConstants]
                internal static class Dcp
                {
                    public const string ContainerKind = "Container_TEST";
                }

                internal static class Caller
                {
                    public static string Get() => {|#0:"Container_TEST"|};
                }
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("Container_TEST", "Dcp.ContainerKind");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task NamespaceScope_InsideMatchingNamespace_Reports()
    {
        // Scoped class only matches when consumer namespace is inside the configured prefix.
        var source = AttributeStub + """

            namespace TestNs.Dcp
            {
                internal static class Caller
                {
                    public static string Get() => {|#0:"DcpKindValue"|};
                }
            }

            namespace OtherNs
            {
                [Aspire.InternalKnownConstants(Namespaces = new[] { "TestNs.Dcp" })]
                internal static class KnownDcpKinds
                {
                    public const string Container = "DcpKindValue";
                }
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("DcpKindValue", "KnownDcpKinds.Container");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task NamespaceScope_OutsideMatchingNamespace_Suppressed()
    {
        // Same scoped class, but the consumer is in a different namespace. The
        // scoped class is the only candidate and it doesn't apply, so no diagnostic.
        var source = AttributeStub + """

            namespace SomeOtherNs
            {
                internal static class Caller
                {
                    public static string Get() => "DcpKindValue2";
                }
            }

            namespace KindsHomeNs
            {
                [Aspire.InternalKnownConstants(Namespaces = new[] { "TestNs.Dcp" })]
                internal static class KnownDcpKinds
                {
                    public const string Container = "DcpKindValue2";
                }
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task NamespaceScope_MostSpecificWins_OverUnscoped()
    {
        // Both an unscoped Known* class and a scoped one match the value. From inside
        // the scoped class's namespace, only the scoped match should be reported.
        var source = AttributeStub + """

            namespace TestNs.Dcp
            {
                internal static class Caller
                {
                    public static string Get() => {|#0:"SharedValue"|};
                }
            }

            namespace AppModelNs
            {
                internal static class KnownResourceTypes
                {
                    public const string Container = "SharedValue";
                }
            }

            namespace KindsHomeNs
            {
                [Aspire.InternalKnownConstants(Namespaces = new[] { "TestNs.Dcp" })]
                internal static class KnownDcpKinds
                {
                    public const string Container = "SharedValue";
                }
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("SharedValue", "KnownDcpKinds.Container");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task NamespaceScope_OutOfScope_FallsBackToUnscoped()
    {
        // Same setup as MostSpecificWins, but the consumer is OUTSIDE the scoped
        // namespace — so the unscoped KnownResourceTypes match takes effect.
        var source = AttributeStub + """

            namespace AppModelNs.Subns
            {
                internal static class Caller
                {
                    public static string Get() => {|#0:"SharedValue2"|};
                }
            }

            namespace AppModelNs
            {
                internal static class KnownResourceTypes
                {
                    public const string Container = "SharedValue2";
                }
            }

            namespace KindsHomeNs
            {
                [Aspire.InternalKnownConstants(Namespaces = new[] { "TestNs.Dcp" })]
                internal static class KnownDcpKinds
                {
                    public const string Container = "SharedValue2";
                }
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("SharedValue2", "KnownResourceTypes.Container");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task UnscopedAmbiguity_AcrossUnrelatedRoots_Suppressed()
    {
        // Two unscoped Known* classes own the same value. The analyzer can't tell
        // which one the developer means, so it must NOT report at Error severity.
        var source = """
            namespace TestNs;

            internal static class KnownAlpha
            {
                public const string Shared = "SharedAcrossDomains";
            }

            internal static class KnownBeta
            {
                public const string Shared = "SharedAcrossDomains";
            }

            internal static class Caller
            {
                public static string Get() => "SharedAcrossDomains";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task SameRootMultiMatch_StillReported()
    {
        // Existing modern + nested Legacy pattern: same outer Known* class, two
        // candidates with the same value. Should still produce one combined
        // diagnostic because the developer can pick either name unambiguously.
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string ResourceUrl = "SAME_ROOT_DUP";

                public static class Legacy
                {
                    public const string ResourceUrl = "SAME_ROOT_DUP";
                }
            }

            internal static class Caller
            {
                public static string Get() => {|#0:"SAME_ROOT_DUP"|};
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("SAME_ROOT_DUP", "KnownConfig.ResourceUrl or KnownConfig.Legacy.ResourceUrl");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task AssemblyScope_NonMatchingAssembly_Suppressed()
    {
        // The CSharpAnalyzerTest harness uses the default assembly name "TestProject".
        // A scoped class limited to a different assembly should not match.
        var source = AttributeStub + """

            namespace TestNs
            {
                [Aspire.InternalKnownConstants(Assemblies = new[] { "Aspire.Cli" })]
                internal static class KnownCliOnly
                {
                    public const string Foo = "AssemblyScopedValue";
                }

                internal static class Caller
                {
                    public static string Get() => "AssemblyScopedValue";
                }
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task AssemblyScope_MatchingAssembly_Reports()
    {
        // The CSharpAnalyzerTest harness uses the default assembly name "TestProject",
        // so scoping to that name should let the diagnostic fire.
        var source = AttributeStub + """

            namespace TestNs
            {
                [Aspire.InternalKnownConstants(Assemblies = new[] { "TestProject" })]
                internal static class KnownTestOnly
                {
                    public const string Foo = "AssemblyScopedValue2";
                }

                internal static class Caller
                {
                    public static string Get() => {|#0:"AssemblyScopedValue2"|};
                }
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("AssemblyScopedValue2", "KnownTestOnly.Foo");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task NamespaceScope_PrefixMatch_NotPartialWord()
    {
        // "TestNs.Dcp" should NOT match a consumer in namespace "TestNs.DcpExtensions"
        // — prefix has to be followed by a '.' or end-of-string.
        var source = AttributeStub + """

            namespace TestNs.DcpExtensions
            {
                internal static class Caller
                {
                    public static string Get() => "PrefixWordValue";
                }
            }

            namespace KindsHomeNs
            {
                [Aspire.InternalKnownConstants(Namespaces = new[] { "TestNs.Dcp" })]
                internal static class KnownDcpKinds
                {
                    public const string Container = "PrefixWordValue";
                }
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }
}
