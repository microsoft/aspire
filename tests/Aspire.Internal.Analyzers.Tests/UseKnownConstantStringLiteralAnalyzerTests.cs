// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Aspire.Internal.Analyzers.Tests;

public class UseKnownConstantStringLiteralAnalyzerTests
{
    [Fact]
    public async Task Literal_MatchingKnownConstant_ReportsDiagnostic()
    {
        // The KnownConfig class is in source: it is a member of the compilation's
        // own assembly, so the catalog picks it up regardless of assembly name.
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string FooBar = "FOO_BAR_CONST";
            }

            internal static class Caller
            {
                public static string Get() => {|#0:"FOO_BAR_CONST"|};
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("FOO_BAR_CONST", "KnownConfig.FooBar");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task Literal_InsideKnownConstantInitializer_NoDiagnostic()
    {
        // The const-field initializer for the constant itself must not be flagged.
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string FooBar = "FOO_BAR_CONST";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task Literal_ShorterThanThreshold_NoDiagnostic()
    {
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string Tiny = "abc";
            }

            internal static class Caller
            {
                public static string Get() => "abc";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task Literal_NotMatchingAnyConstant_NoDiagnostic()
    {
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string FooBar = "FOO_BAR_CONST";
            }

            internal static class Caller
            {
                public static string Get() => "SOMETHING_ELSE_ENTIRELY";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task IdentifierReference_ToConstant_NoDiagnostic()
    {
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string FooBar = "FOO_BAR_CONST";
            }

            internal static class Caller
            {
                public static string Get() => KnownConfig.FooBar;
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task Literal_MatchingFieldName_NotValue_NoDiagnostic()
    {
        // The literal "FooBar" matches the *name* of a field, not its value.
        // The analyzer keys by value only, so this must not be flagged.
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string FooBar = "FOO_BAR_CONST";
            }

            internal static class Caller
            {
                public static string Get() => "FooBar";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task Literal_MatchingMultipleConstants_DiagnosticListsAll()
    {
        // Mirrors the modern + Legacy pattern in src/Shared/KnownConfigNames.cs:
        // both classes can legitimately define the same value. The diagnostic
        // should mention all matches so the developer can pick the right one.
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string ResourceUrl = "ASPIRE_RESOURCE_URL_VALUE";

                public static class Legacy
                {
                    public const string ResourceUrl = "ASPIRE_RESOURCE_URL_VALUE";
                }
            }

            internal static class Caller
            {
                public static string Get() => {|#0:"ASPIRE_RESOURCE_URL_VALUE"|};
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("ASPIRE_RESOURCE_URL_VALUE", "KnownConfig.ResourceUrl or KnownConfig.Legacy.ResourceUrl");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task NonStaticKnownClass_NotPickedUpByConvention_NoDiagnostic()
    {
        // KnownConfig here is NOT static, so it's not part of the convention.
        var source = """
            namespace TestNs;

            internal class KnownConfig
            {
                public const string FooBar = "FOO_BAR_NON_STATIC";
            }

            internal static class Caller
            {
                public static string Get() => "FOO_BAR_NON_STATIC";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task ClassNotNamedKnown_NotPickedUpByConvention_NoDiagnostic()
    {
        var source = """
            namespace TestNs;

            internal static class ConfigStrings
            {
                public const string FooBar = "FOO_BAR_NOT_KNOWN";
            }

            internal static class Caller
            {
                public static string Get() => "FOO_BAR_NOT_KNOWN";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task ConstantWithNonStringType_Ignored()
    {
        // Only public const string members participate in the catalog.
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const int FooBar = 42;
            }

            internal static class Caller
            {
                public static int Get() => 42;
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task Literal_InsideKnownClass_NoDiagnostic()
    {
        // Helper methods inside the same Known* class shouldn't be told to
        // reference themselves.
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                public const string FooBar = "FOO_BAR_CONST";

                public static string GetDefault() => "FOO_BAR_CONST";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task NestedKnownStaticClass_PickedUp()
    {
        // Nested Known* class is reachable; its constants should be discovered.
        var source = """
            namespace TestNs;

            internal static class Outer
            {
                public static class KnownInner
                {
                    public const string FooBar = "FOO_BAR_NESTED";
                }
            }

            internal static class Caller
            {
                public static string Get() => {|#0:"FOO_BAR_NESTED"|};
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("FOO_BAR_NESTED", "Outer.KnownInner.FooBar");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }

    [Fact]
    public async Task PrivateConstant_NotSuggested()
    {
        // A private const is an implementation detail and not callable from
        // a sibling/external type — don't suggest it as a replacement.
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                private const string SecretValue = "SECRET_PRIVATE_VALUE";

                public static bool Match(string s) => s == SecretValue;
            }

            internal static class Caller
            {
                public static string Get() => "SECRET_PRIVATE_VALUE";
            }
            """;

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, []).RunAsync();
    }

    [Fact]
    public async Task PrivateConstant_DoesNotMaskPublicConstantWithSameValue()
    {
        // When a private const and a public const share the same value, the public
        // one is still suggested. (Catalog skips the private; analyzer reports the public.)
        var source = """
            namespace TestNs;

            internal static class KnownConfig
            {
                private const string PrivateName = "SHARED_VALUE_NAME";
                public const string PublicName = "SHARED_VALUE_NAME";
            }

            internal static class Caller
            {
                public static string Get() => {|#0:"SHARED_VALUE_NAME"|};
            }
            """;

        var expected = DiagnosticResult.CompilerError(UseKnownConstantStringLiteralAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithArguments("SHARED_VALUE_NAME", "KnownConfig.PublicName");

        await AnalyzerTest.Create<UseKnownConstantStringLiteralAnalyzer>(source, [expected]).RunAsync();
    }
}
