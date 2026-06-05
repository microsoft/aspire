// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using System.Text.RegularExpressions;

namespace QuarantineTools.Tests;

public class QuarantineScriptTests
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
    public void Quarantine_AddsAttribute_WhenMissing(string code, string fullName, string issue)
    {
        var updated = Quarantine(fullName, issue, code);
        Assert.Contains("QuarantinedTest", updated);
        Assert.Contains(issue, updated);
        // Attribute should be applied at method level and not duplicate facts
        Assert.Contains("[Fact]", updated);
    }

    [Fact]
    public void Quarantine_IsIdempotent_DoesNotDuplicateOrChangeReason()
    {
        const string originalUrl = "https://github.com/microsoft/aspire/issues/100";
        const string newUrl = "https://github.com/microsoft/aspire/issues/200";
        const string code = """
        namespace N;
        using Xunit;
        using Aspire.TestUtilities;
        public class C {
            [Fact]
            [QuarantinedTest("https://github.com/microsoft/aspire/issues/100")]
            public void M() { }
        }
        """;

        var updated = Quarantine("N.C.M", newUrl, code);
        var count = Regex.Matches(updated, "QuarantinedTest").Count;
        Assert.Equal(1, count);
        Assert.Contains(originalUrl, updated);
        Assert.DoesNotContain(newUrl, updated);
    }

    [Fact]
    public void Unquarantine_RemovesAttribute_WhenPresent()
    {
        const string code = """
        namespace N;
        using Xunit;
        using Aspire.TestUtilities;
        public class C {
            [Fact]
            [QuarantinedTest("https://github.com/microsoft/aspire/issues/3")]
            public void M() { }
        }
        """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "M");
        var updated = RemoveQuarantinedAttribute(method, out var removed);
        Assert.True(removed);
        var newRoot = root.ReplaceNode(method, updated);
        var text = newRoot.ToFullString();
        Assert.DoesNotContain("QuarantinedTest", text);
        Assert.Contains("[Fact]", text);
    }

    [Theory]
    [InlineData("http://example.com/issue/1", true)]
    [InlineData("https://github.com/microsoft/aspire/issues/123", true)]
    [InlineData("ftp://example.com/issue/1", false)]
    [InlineData("www.github.com/issue/1", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    public void UrlValidation_Works(string url, bool expected)
    {
        var result = IsHttpUrl(url);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Quarantine_AddsUsingDirective_WhenMissing()
    {
        const string code = """
        namespace N;
        using Xunit;
        public class C { [Fact] public void M() { } }
        """;

        var updated = Quarantine("N.C.M", "https://github.com/microsoft/aspire/issues/500", code);
        Assert.Contains("using Aspire.TestUtilities;", updated);
        Assert.Contains("[QuarantinedTest(\"https://github.com/microsoft/aspire/issues/500\")]", updated);
    }

    [Fact]
    public void Unquarantine_RemovesUsingDirective_WhenNoAttributesRemain()
    {
        const string code = """
        namespace N;
        using Xunit;
        using Aspire.TestUtilities;
        public class C {
            [Fact]
            [QuarantinedTest("https://github.com/microsoft/aspire/issues/3")]
            public void M() { }
        }
        """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "M");
        var updatedMethod = RemoveQuarantinedAttribute(method, out var removed);
        Assert.True(removed);
        var newRoot = root.ReplaceNode(method, updatedMethod);
        // After removal, simulate the same cleanup as the tool would
        bool anyQuarantinedLeft = newRoot.DescendantNodes().OfType<AttributeSyntax>().Any(IsQuarantinedAttribute);
        if (!anyQuarantinedLeft)
        {
            newRoot = RemoveUsingDirective(newRoot, "Aspire.TestUtilities");
        }
        var text = newRoot.ToFullString();
        Assert.DoesNotContain("using Aspire.TestUtilities;", text);
    }

    [Fact]
    public void Multiple_Targets_AreMatched_ByEnclosingNamesAndMethod()
    {
        const string code = """
        namespace N1;
        using Xunit;
        public class A { [Fact] public void M() { } }
        namespace N2 { public class B { [Fact] public void M() { } } }
        """;

        var updated1 = Quarantine("N1.A.M", "https://github.com/microsoft/aspire/issues/10", code);
        Assert.Contains("[QuarantinedTest(\"https://github.com/microsoft/aspire/issues/10\")]", updated1);
        // Ensure only the N1.A.M got the attribute, not N2.B.M
        var tree = CSharpSyntaxTree.ParseText(updated1);
        var root = tree.GetCompilationUnitRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var aM = methods.First(m => m.Identifier.ValueText == "M" && GetEnclosingNames(m).Namespace == "N1");
        var bM = methods.First(m => m.Identifier.ValueText == "M" && GetEnclosingNames(m).Namespace == "N2");
        Assert.Contains("QuarantinedTest", aM.ToFullString());
        Assert.DoesNotContain("QuarantinedTest", bM.ToFullString());
    }

    [Fact]
    public void Multiple_Targets_SameFile_UsingDirective_AddedOnce()
    {
        const string code = """
        namespace N;
        using Xunit;
        public class C {
            [Fact] public void M1() { }
            [Fact] public void M2() { }
        }
        """;

        var updated1 = Quarantine("N.C.M1", "https://github.com/microsoft/aspire/issues/11", code);
        var updated2 = Quarantine("N.C.M2", "https://github.com/microsoft/aspire/issues/11", updated1);
        var norm = NormalizeNewlines(updated2);
    // Only one using should be present
        var count = Regex.Matches(norm, "using Aspire.TestUtilities;").Count;
        Assert.Equal(1, count);
    // Both methods should be quarantined (ignore indentation)
    var rx1 = new Regex(@"\[QuarantinedTest\(""https://github.com/microsoft/aspire/issues/11""\)\]\n\s*public void M1\(\)", RegexOptions.Multiline);
    var rx2 = new Regex(@"\[QuarantinedTest\(""https://github.com/microsoft/aspire/issues/11""\)\]\n\s*public void M2\(\)", RegexOptions.Multiline);
    Assert.True(rx1.IsMatch(norm), $"Expected to match M1 pattern, but did not.\nPattern: {rx1}\nText:\n{norm}");
    Assert.True(rx2.IsMatch(norm), $"Expected to match M2 pattern, but did not.\nPattern: {rx2}\nText:\n{norm}");
    }

    [Fact]
    public void Quarantine_DoesNotInsertBlankLine_BetweenAttributes()
    {
        const string code = """
        namespace N;
        using Xunit;
        public class C {
            [Fact]
            public void M() { }
        }
        """;

        var updated = Quarantine("N.C.M", "https://github.com/microsoft/aspire/issues/99", code);
        var norm = NormalizeNewlines(updated);
    // Ensure there is no blank line between [Fact] and [QuarantinedTest]
    Assert.DoesNotMatch(new Regex(@"\[Fact\]\n\s*\n\s*\[QuarantinedTest", RegexOptions.Multiline), norm);
    // But [Fact] followed by [QuarantinedTest(...)] on the next line should exist (ignore indentation)
    Assert.Matches(new Regex(@"\[Fact\]\n\s+\[QuarantinedTest\(""https://github.com/microsoft/aspire/issues/99""\)\]", RegexOptions.Multiline), norm);
    }

    private static string Quarantine(string fullMethodName, string issueUrl, string code)
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

            var updated = AddQuarantinedAttribute(method, issueUrl);
            root = root.ReplaceNode(method, updated);
            // Simulate the tool adding using directive for short attribute name
            root = EnsureUsingDirective(root, "Aspire.TestUtilities");
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

    private static bool IsQuarantinedAttribute(AttributeSyntax attr)
    {
        string lastId = attr.Name switch
        {
            IdentifierNameSyntax ins => ins.Identifier.ValueText,
            QualifiedNameSyntax qns => (qns.Right as IdentifierNameSyntax)?.Identifier.ValueText ?? qns.Right.ToString(),
            AliasQualifiedNameSyntax aqn => (aqn.Name as IdentifierNameSyntax)?.Identifier.ValueText ?? aqn.Name.ToString(),
            _ => attr.Name.ToString().Split('.').Last()
        };
        return string.Equals(lastId, "QuarantinedTest", StringComparison.Ordinal)
            || string.Equals(lastId, "QuarantinedTestAttribute", StringComparison.Ordinal);
    }

    private static MethodDeclarationSyntax RemoveQuarantinedAttribute(MethodDeclarationSyntax method, out bool removed)
    {
        removed = false;
        if (method.AttributeLists.Count == 0)
        {
            return method;
        }

        var newLists = new List<AttributeListSyntax>();
        foreach (var list in method.AttributeLists)
        {
            var remaining = list.Attributes.Where(a => !IsQuarantinedAttribute(a)).ToList();
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

    private static MethodDeclarationSyntax AddQuarantinedAttribute(MethodDeclarationSyntax method, string issueUrl)
    {
        foreach (var list in method.AttributeLists)
        {
            if (list.Attributes.Any(IsQuarantinedAttribute))
            {
                return method;
            }
        }
        // Use short attribute name and simulate the tool adding the using at file level
        var attrName = SyntaxFactory.ParseName("QuarantinedTest");
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

    private static bool IsHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static CompilationUnitSyntax EnsureUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        if (root.Usings.Any(u => u.Name != null && u.Name.ToString() == namespaceName))
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

    private static CompilationUnitSyntax RemoveUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        // Remove matching using directives wherever they appear in the tree
        var nodesToRemove = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => u.Name != null && u.Name.ToString() == namespaceName)
            .ToList();
        CompilationUnitSyntax updated;
        if (nodesToRemove.Count > 0)
        {
            updated = (CompilationUnitSyntax)root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            updated = root;
        }

        // Also ensure the compilation unit usings are filtered (in case any remain)
        if (updated.Usings.Count > 0)
        {
            var filtered = updated.Usings.Where(u => u.Name == null || u.Name.ToString() != namespaceName).ToList();
            updated = updated.WithUsings(SyntaxFactory.List(filtered));
        }

        // Fallback: if the text still contains the using (due to trivia/layout quirks), do a textual removal
        var text = updated.ToFullString();
        if (text.Contains($"using {namespaceName};"))
        {
            text = text.Replace($"using {namespaceName};\r\n", string.Empty)
                       .Replace($"using {namespaceName};\n", string.Empty);
            updated = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        }

        return updated;
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");
}
