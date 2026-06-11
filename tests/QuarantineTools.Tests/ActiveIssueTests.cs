// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using System.Text.RegularExpressions;

namespace QuarantineTools.Tests;

public class ActiveIssueTests
{
    [Theory]
    [InlineData(
        """
        namespace N1.N2;
        using Xunit;
        public class C {
            [Fact]
            public void M() { }
        }
        """,
        "N1.N2.C.M",
        "https://github.com/microsoft/aspire/issues/1")]
    [InlineData(
        """
        namespace N1.N2
        {
            using Xunit;
            public class Outer {
                public class Inner {
                    [Fact]
                    public void M() { }
                }
            }
        }
        """,
        "N1.N2.Outer+Inner.M",
        "https://github.com/microsoft/aspire/issues/2")]
    public void ActiveIssue_AddsAttribute_WhenMissing(string code, string fullName, string issue)
    {
        var updated = AddActiveIssue(fullName, issue, code);
        Assert.Contains("ActiveIssue", updated);
        Assert.Contains(issue, updated);
        // Attribute should be applied at method level and not duplicate facts
        Assert.Contains("[Fact]", updated);
    }

    [Fact]
    public void ActiveIssue_IsIdempotent_DoesNotDuplicateOrChangeReason()
    {
        const string originalUrl = "https://github.com/microsoft/aspire/issues/100";
        const string newUrl = "https://github.com/microsoft/aspire/issues/200";
        const string code = """
        namespace N;
        using Xunit;
        public class C {
            [Fact]
            [ActiveIssue("https://github.com/microsoft/aspire/issues/100")]
            public void M() { }
        }
        """;

        var updated = AddActiveIssue("N.C.M", newUrl, code);
        var count = Regex.Matches(updated, "ActiveIssue").Count;
        Assert.Equal(1, count);
        Assert.Contains(originalUrl, updated);
        Assert.DoesNotContain(newUrl, updated);
    }

    [Fact]
    public void ActiveIssue_RemovesAttribute_WhenPresent()
    {
        const string code = """
        namespace N;
        using Xunit;
        public class C {
            [Fact]
            [ActiveIssue("https://github.com/microsoft/aspire/issues/3")]
            public void M() { }
        }
        """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "M");
        var updated = RemoveActiveIssueAttribute(method, out var removed);
        Assert.True(removed);
        var newRoot = root.ReplaceNode(method, updated);
        var text = newRoot.ToFullString();
        Assert.DoesNotContain("ActiveIssue", text);
        Assert.Contains("[Fact]", text);
    }

    [Fact]
    public void ActiveIssue_AddsUsingDirective_WhenMissing()
    {
        const string code = """
        namespace N;
        public class C { public void M() { } }
        """;

        var updated = AddActiveIssue("N.C.M", "https://github.com/microsoft/aspire/issues/500", code);
        Assert.Contains("using Xunit;", updated);
        Assert.Contains("[ActiveIssue(\"https://github.com/microsoft/aspire/issues/500\")]", updated);
    }

    [Fact]
    public void ActiveIssue_DoesNotDuplicateUsingDirective_WhenAlreadyPresent()
    {
        const string code = """
        namespace N;
        using Xunit;
        public class C { 
            [Fact]
            public void M() { } 
        }
        """;

        var updated = AddActiveIssue("N.C.M", "https://github.com/microsoft/aspire/issues/501", code);
        var norm = NormalizeNewlines(updated);
        var count = Regex.Matches(norm, "using Xunit;").Count;
        Assert.Equal(1, count);
    }

    [Fact]
    public void ActiveIssue_WithConditionalArguments()
    {
        const string code = """
        namespace N;
        using Xunit;
        public class C {
            [Fact]
            public void M() { }
        }
        """;

        // ActiveIssue can have conditional arguments like typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo)
        // For simplicity, the tool adds just the URL, but the attribute should support additional arguments
        var updated = AddActiveIssue("N.C.M", "https://github.com/microsoft/aspire/issues/11820", code);
        Assert.Contains("[ActiveIssue(\"https://github.com/microsoft/aspire/issues/11820\")]", updated);
    }

    [Fact]
    public void ActiveIssue_Multiple_Targets_SameFile_UsingDirective_AddedOnce()
    {
        const string code = """
        namespace N;
        public class C {
            public void M1() { }
            public void M2() { }
        }
        """;

        var updated1 = AddActiveIssue("N.C.M1", "https://github.com/microsoft/aspire/issues/11", code);
        var updated2 = AddActiveIssue("N.C.M2", "https://github.com/microsoft/aspire/issues/12", updated1);
        var norm = NormalizeNewlines(updated2);
        // Only one using should be present
        var count = Regex.Matches(norm, "using Xunit;").Count;
        Assert.Equal(1, count);
        // Both methods should have ActiveIssue
        Assert.Contains("M1", updated2);
        Assert.Contains("M2", updated2);
        var activeIssueCount = Regex.Matches(norm, @"\[ActiveIssue\(").Count;
        Assert.Equal(2, activeIssueCount);
    }

    [Fact]
    public void ActiveIssue_DoesNotInsertBlankLine_BetweenAttributes()
    {
        const string code = """
        namespace N;
        using Xunit;
        public class C {
            [Fact]
            public void M() { }
        }
        """;

        var updated = AddActiveIssue("N.C.M", "https://github.com/microsoft/aspire/issues/99", code);
        var norm = NormalizeNewlines(updated);
        // Ensure there is no blank line between [Fact] and [ActiveIssue]
        Assert.DoesNotMatch(new Regex(@"\[Fact\]\n\s*\n\s*\[ActiveIssue", RegexOptions.Multiline), norm);
        // But [Fact] followed by [ActiveIssue(...)] on the next line should exist (ignore indentation)
        Assert.Matches(new Regex(@"\[Fact\]\n\s+\[ActiveIssue\(""https://github.com/microsoft/aspire/issues/99""\)\]", RegexOptions.Multiline), norm);
    }

    [Fact]
    public void ActiveIssue_RecognizesAttributeWithOrWithoutSuffix()
    {
        const string codeWithSuffix = """
        namespace N;
        using Xunit;
        public class C {
            [Fact]
            [ActiveIssueAttribute("https://github.com/microsoft/aspire/issues/123")]
            public void M() { }
        }
        """;

        const string codeWithoutSuffix = """
        namespace N;
        using Xunit;
        public class C {
            [Fact]
            [ActiveIssue("https://github.com/microsoft/aspire/issues/124")]
            public void M() { }
        }
        """;

        // Both should be recognized as already having the attribute
        var updatedWithSuffix = AddActiveIssue("N.C.M", "https://github.com/microsoft/aspire/issues/999", codeWithSuffix);
        var updatedWithoutSuffix = AddActiveIssue("N.C.M", "https://github.com/microsoft/aspire/issues/999", codeWithoutSuffix);

        // Should not add another attribute (idempotent)
        var countWithSuffix = Regex.Matches(updatedWithSuffix, @"ActiveIssue").Count;
        var countWithoutSuffix = Regex.Matches(updatedWithoutSuffix, @"ActiveIssue").Count;

        Assert.Equal(1, countWithSuffix);
        Assert.Equal(1, countWithoutSuffix);
    }

    [Fact]
    public void ActiveIssue_RemovalDoesNotAffectOtherAttributes()
    {
        const string code = """
        namespace N;
        using Xunit;
        public class C {
            [Fact]
            [ActiveIssue("https://github.com/microsoft/aspire/issues/3")]
            [Trait("Category", "Integration")]
            public void M() { }
        }
        """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "M");
        var updated = RemoveActiveIssueAttribute(method, out var removed);
        Assert.True(removed);
        var newRoot = root.ReplaceNode(method, updated);
        var text = newRoot.ToFullString();
        
        Assert.DoesNotContain("ActiveIssue", text);
        Assert.Contains("[Fact]", text);
        Assert.Contains("[Trait(\"Category\", \"Integration\")]", text);
    }

    [Fact]
    public void ActiveIssue_CanCoexist_WithQuarantinedTest()
    {
        // Verify that having both attribute types in the same file works correctly
        const string code = """
        namespace N;
        using Xunit;
        using Aspire.TestUtilities;
        public class C {
            [Fact]
            [QuarantinedTest("https://github.com/microsoft/aspire/issues/1")]
            public void M1() { }
            
            [Fact]
            public void M2() { }
        }
        """;

        var updated = AddActiveIssue("N.C.M2", "https://github.com/microsoft/aspire/issues/2", code);
        
        // Should have both attributes for different methods
        Assert.Contains("QuarantinedTest", updated);
        Assert.Contains("ActiveIssue", updated);
        Assert.Contains("using Xunit;", updated);
        Assert.Contains("using Aspire.TestUtilities;", updated);
    }

    private static string AddActiveIssue(string fullMethodName, string issueUrl, string code)
    {
        var (pathParts, methodName) = ParseFullMethodName(fullMethodName);
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();
        var methodNodes = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == methodName).ToList();

        foreach (var method in methodNodes)
        {
            var (ns, typeChain) = GetEnclosingNames(method);
            var actualParts = new List<string>();
            if (!string.IsNullOrEmpty(ns))
            {
                actualParts.AddRange(ns.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            actualParts.AddRange(typeChain);
            if (!SequenceEquals(actualParts, pathParts))
            {
                continue;
            }

            var updated = AddActiveIssueAttribute(method, issueUrl);
            root = root.ReplaceNode(method, updated);
            // Simulate the tool adding using directive for Xunit
            root = EnsureUsingDirective(root, "Xunit");
            break;
        }

        return root.ToFullString();
    }

    // Helpers copied from the script to validate logic
    private static (List<string> PathPartsBeforeMethod, string Method) ParseFullMethodName(string input)
    {
        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var method = parts[^1];
        var beforeMethod = parts.Take(parts.Length - 1)
            .SelectMany(p => p.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        return (beforeMethod, method);
    }

    private static (string Namespace, List<string> TypeChain) GetEnclosingNames(SyntaxNode node)
    {
        var typeNames = new List<string>();
        string ns = string.Empty;

        for (var current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case ClassDeclarationSyntax cd:
                    typeNames.Insert(0, cd.Identifier.ValueText);
                    break;
                case StructDeclarationSyntax sd:
                    typeNames.Insert(0, sd.Identifier.ValueText);
                    break;
                case RecordDeclarationSyntax rd:
                    typeNames.Insert(0, rd.Identifier.ValueText);
                    break;
                case InterfaceDeclarationSyntax id:
                    typeNames.Insert(0, id.Identifier.ValueText);
                    break;
                case NamespaceDeclarationSyntax nd:
                    ns = nd.Name.ToString();
                    current = null;
                    break;
                case FileScopedNamespaceDeclarationSyntax fsn:
                    ns = fsn.Name.ToString();
                    current = null;
                    break;
            }
            if (current == null)
            {
                break;
            }
        }
        return (ns, typeNames);
    }

    private static bool SequenceEquals(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsActiveIssueAttribute(AttributeSyntax attr)
    {
        string lastId = attr.Name switch
        {
            IdentifierNameSyntax ins => ins.Identifier.ValueText,
            QualifiedNameSyntax qns => (qns.Right as IdentifierNameSyntax)?.Identifier.ValueText ?? qns.Right.ToString(),
            AliasQualifiedNameSyntax aqn => (aqn.Name as IdentifierNameSyntax)?.Identifier.ValueText ?? aqn.Name.ToString(),
            _ => attr.Name.ToString().Split('.').Last()
        };
        return string.Equals(lastId, "ActiveIssue", StringComparison.Ordinal)
            || string.Equals(lastId, "ActiveIssueAttribute", StringComparison.Ordinal);
    }

    private static MethodDeclarationSyntax RemoveActiveIssueAttribute(MethodDeclarationSyntax method, out bool removed)
    {
        removed = false;
        if (method.AttributeLists.Count == 0)
        {
            return method;
        }

        var newLists = new List<AttributeListSyntax>();
        foreach (var list in method.AttributeLists)
        {
            var remaining = list.Attributes.Where(a => !IsActiveIssueAttribute(a)).ToList();
            if (remaining.Count == list.Attributes.Count)
            {
                newLists.Add(list);
                continue;
            }
            removed = true;
            if (remaining.Count > 0)
            {
                var newList = list.WithAttributes(SyntaxFactory.SeparatedList(remaining));
                newLists.Add(newList);
            }
        }

        return removed ? method.WithAttributeLists(SyntaxFactory.List(newLists)) : method;
    }

    private static MethodDeclarationSyntax AddActiveIssueAttribute(MethodDeclarationSyntax method, string issueUrl)
    {
        foreach (var list in method.AttributeLists)
        {
            if (list.Attributes.Any(IsActiveIssueAttribute))
            {
                return method;
            }
        }
        // Use short attribute name (ActiveIssue, not ActiveIssueAttribute)
        var attrName = SyntaxFactory.ParseName("ActiveIssue");
        var attrArgs = string.IsNullOrWhiteSpace(issueUrl)
            ? null
            : SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.AttributeArgument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(issueUrl)))));

        var attr = SyntaxFactory.Attribute(attrName, attrArgs);
        var newList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attr));

        if (method.AttributeLists.Count > 0)
        {
            // Append after existing attributes ensuring exactly one newline between them.
            var last = method.AttributeLists[method.AttributeLists.Count - 1];
            var indentation = SyntaxFactory.TriviaList(last.GetLeadingTrivia().Where(t => !t.IsKind(SyntaxKind.EndOfLineTrivia)));
            bool lastEndsWithNewline = last.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            var leading = lastEndsWithNewline
                ? indentation
                : indentation.Add(SyntaxFactory.EndOfLine("\n"));
            newList = newList
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
        }
        else
        {
            var leading = method.GetLeadingTrivia();
            newList = newList.WithLeadingTrivia(leading)
                             .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
        }

        var newLists = method.AttributeLists.Add(newList);
        var updated = method.WithAttributeLists(newLists);
        return updated;
    }

    private static CompilationUnitSyntax EnsureUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        // Check if using already exists anywhere in the tree (compilation unit or namespace level)
        var allUsings = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => u.Name != null && u.Name.ToString() == namespaceName);
        
        if (allUsings.Any())
        {
            return root;
        }
        
        var usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName))
            .WithUsingKeyword(
                SyntaxFactory.Token(SyntaxKind.UsingKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space))
            .WithSemicolonToken(
                SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                    .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")));
        return root.WithUsings(root.Usings.Add(usingDirective));
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");
}
