// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Templating;

namespace Aspire.Cli.Tests.Templating;

public class ConditionalBlockProcessorTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Basic include / exclude
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Include_KeepsContent_RemovesMarkers()
    {
        var input = """
            before
            // {{#feature}}
            included content
            // {{/feature}}
            after
            """;

        var result = ConditionalBlockProcessor.ProcessBlock(input, "feature", include: true);

        Assert.Contains("included content", result);
        Assert.DoesNotContain("{{#feature}}", result);
        Assert.DoesNotContain("{{/feature}}", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void Exclude_RemovesContentAndMarkers()
    {
        var input = """
            before
            // {{#feature}}
            excluded content
            // {{/feature}}
            after
            """;

        var result = ConditionalBlockProcessor.ProcessBlock(input, "feature", include: false);

        Assert.DoesNotContain("excluded content", result);
        Assert.DoesNotContain("{{#feature}}", result);
        Assert.DoesNotContain("{{/feature}}", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Comment style variations
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Handles_DoubleSlash_CommentMarkers()
    {
        var input = "line1\n// {{#test}}\nkept\n// {{/test}}\nline2\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("line1\nkept\nline2\n", result);
    }

    [Fact]
    public void Handles_Hash_CommentMarkers()
    {
        var input = "line1\n# {{#test}}\nkept\n# {{/test}}\nline2\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("line1\nkept\nline2\n", result);
    }

    [Fact]
    public void Handles_Bare_Markers_WithoutCommentPrefix()
    {
        var input = "line1\n{{#test}}\nkept\n{{/test}}\nline2\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("line1\nkept\nline2\n", result);
    }

    [Fact]
    public void Handles_IndentedMarkers()
    {
        var input = "line1\n    // {{#test}}\n    kept\n    // {{/test}}\nline2\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("line1\n    kept\nline2\n", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Multiple blocks of same type
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Include_MultipleBlocks_AllKept()
    {
        var input = "a\n// {{#x}}\nb\n// {{/x}}\nc\n// {{#x}}\nd\n// {{/x}}\ne\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "x", include: true);

        Assert.Equal("a\nb\nc\nd\ne\n", result);
    }

    [Fact]
    public void Exclude_MultipleBlocks_AllRemoved()
    {
        var input = "a\n// {{#x}}\nb\n// {{/x}}\nc\n// {{#x}}\nd\n// {{/x}}\ne\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "x", include: false);

        Assert.Equal("a\nc\ne\n", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Edge cases: position in file
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Block_AtStartOfFile()
    {
        // Exercises LastIndexOf returning -1 for start-of-file boundary.
        var input = "// {{#test}}\nfirst\n// {{/test}}\nafter\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("first\nafter\n", result);
    }

    [Fact]
    public void Block_AtEndOfFile_NoTrailingNewline()
    {
        // Exercises IndexOf returning content.Length for end-of-file boundary.
        var input = "before\n// {{#test}}\nlast\n// {{/test}}";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("before\nlast\n", result);
    }

    [Fact]
    public void Block_IsEntireFile_Exclude()
    {
        var input = "// {{#test}}\nall content\n// {{/test}}\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: false);

        Assert.Equal("", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Edge cases: content variations
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyBlock_ProducesSameResultForIncludeAndExclude()
    {
        var input = "before\n// {{#test}}\n// {{/test}}\nafter\n";

        var includeResult = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);
        var excludeResult = ConditionalBlockProcessor.ProcessBlock(input, "test", include: false);

        Assert.Equal("before\nafter\n", includeResult);
        Assert.Equal(includeResult, excludeResult);
    }

    [Fact]
    public void MultilineBlockContent_Include()
    {
        var input = "before\n// {{#test}}\nline1\nline2\nline3\n// {{/test}}\nafter\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("before\nline1\nline2\nline3\nafter\n", result);
    }

    [Fact]
    public void ContentWithCurlyBraces_NotConfusedWithMarkers()
    {
        var input = "before\n// {{#test}}\nvar x = { key: \"value\" };\n// {{/test}}\nafter\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("before\nvar x = { key: \"value\" };\nafter\n", result);
    }

    [Fact]
    public void ContentWithTemplateTokens_NotConfusedWithMarkers()
    {
        var input = "before\n// {{#test}}\nname: {{projectName}}\n// {{/test}}\nafter\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("before\nname: {{projectName}}\nafter\n", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // No markers / no matching markers
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoMarkers_ReturnsUnchanged()
    {
        var input = "just some content\nwith no markers\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal(input, result);
    }

    [Fact]
    public void DifferentBlockName_IgnoresNonMatchingMarkers()
    {
        var input = "before\n// {{#other}}\nkept\n// {{/other}}\nafter\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: false);

        Assert.Equal(input, result);
    }

    [Fact]
    public void MissingEndMarker_ThrowsInvalidOperationException()
    {
        var input = "before\n// {{#test}}\ncontent\nafter\n";

        Assert.Throws<InvalidOperationException>(
            () => ConditionalBlockProcessor.ProcessBlock(input, "test", include: false));
    }

    [Fact]
    public void MissingStartMarker_LeavesContentUnchanged()
    {
        var input = "before\ncontent\n// {{/test}}\nafter\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: false);

        Assert.Equal(input, result);
    }

    [Fact]
    public void EmptyContent_ReturnsEmpty()
    {
        var result = ConditionalBlockProcessor.ProcessBlock("", "test", include: true);

        Assert.Equal("", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Process() with multiple conditions
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Process_MultipleConditions_IncludesAndExcludesCorrectly()
    {
        var input = """
            start
            // {{#redis}}
            redis stuff
            // {{/redis}}
            middle
            // {{#no-redis}}
            no redis stuff
            // {{/no-redis}}
            end
            """;

        var conditions = new Dictionary<string, bool>
        {
            ["redis"] = true,
            ["no-redis"] = false,
        };

        var result = ConditionalBlockProcessor.Process(input, conditions);

        Assert.Contains("redis stuff", result);
        Assert.DoesNotContain("no redis stuff", result);
        Assert.Contains("start", result);
        Assert.Contains("middle", result);
        Assert.Contains("end", result);
    }

    [Fact]
    public void Process_InverseConditions_IncludesAndExcludesCorrectly()
    {
        var input = """
            start
            // {{#redis}}
            redis stuff
            // {{/redis}}
            middle
            // {{#no-redis}}
            no redis stuff
            // {{/no-redis}}
            end
            """;

        var conditions = new Dictionary<string, bool>
        {
            ["redis"] = false,
            ["no-redis"] = true,
        };

        var result = ConditionalBlockProcessor.Process(input, conditions);

        // "no redis stuff" contains the substring "redis stuff", so assert on the full line.
        Assert.DoesNotContain("// {{#redis}}", result);
        Assert.Contains("no redis stuff", result);
        Assert.Contains("start", result);
        Assert.Contains("middle", result);
        Assert.Contains("end", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Adjacent blocks
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AdjacentBlocks_ExcludeBoth()
    {
        var input = "before\n// {{#a}}\nalpha\n// {{/a}}\n// {{#b}}\nbeta\n// {{/b}}\nafter\n";

        var conditions = new Dictionary<string, bool>
        {
            ["a"] = false,
            ["b"] = false,
        };

        var result = ConditionalBlockProcessor.Process(input, conditions);

        Assert.DoesNotContain("alpha", result);
        Assert.DoesNotContain("beta", result);
        Assert.Equal("before\nafter\n", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Realistic template scenarios
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void PythonFile_WithRedisAndNoRedisBlocks()
    {
        var input = """
            import os
            # {{#redis}}
            import redis
            # {{/redis}}

            # {{#redis}}
            def get_client():
                return redis.from_url(os.environ.get("CACHE_URI"))
            # {{/redis}}

            # {{#no-redis}}
            def get_forecast():
                return generate_fresh()
            # {{/no-redis}}
            """;

        var conditions = new Dictionary<string, bool>
        {
            ["redis"] = true,
            ["no-redis"] = false,
        };

        var result = ConditionalBlockProcessor.Process(input, conditions);

        Assert.Contains("import redis", result);
        Assert.Contains("def get_client():", result);
        Assert.DoesNotContain("def get_forecast():", result);
        Assert.DoesNotContain("{{#", result);
        Assert.DoesNotContain("{{/", result);
    }

    [Fact]
    public void PyprojectToml_DependenciesBlock()
    {
        var input = """
            dependencies = [
                "fastapi[standard]>=0.119.0",
                "opentelemetry-distro>=0.59b0",
            # {{#redis}}
                "opentelemetry-instrumentation-redis>=0.59b0",
                "redis>=6.4.0",
            # {{/redis}}
            ]
            """;

        var resultWith = ConditionalBlockProcessor.ProcessBlock(input, "redis", include: true);
        Assert.Contains("redis>=6.4.0", resultWith);
        Assert.DoesNotContain("{{#redis}}", resultWith);
        // Verify the closing bracket is still there
        Assert.Contains("]", resultWith);

        var resultWithout = ConditionalBlockProcessor.ProcessBlock(input, "redis", include: false);
        Assert.DoesNotContain("redis", resultWithout);
        Assert.Contains("fastapi", resultWithout);
        Assert.Contains("]", resultWithout);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Whitespace / formatting preservation
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreservesIndentation_InKeptContent()
    {
        var input = "    // {{#test}}\n        indented content\n    // {{/test}}\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: true);

        Assert.Equal("        indented content\n", result);
    }

    [Fact]
    public void SurroundingBlankLines_PreservedWhenExcluding()
    {
        var input = "before\n\n// {{#test}}\ncontent\n// {{/test}}\n\nafter\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "test", include: false);

        // The processor removes the block and its markers but preserves surrounding blank lines.
        Assert.Equal("before\n\n\nafter\n", result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Block name edge cases
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void BlockNameWithHyphen_WorksCorrectly()
    {
        var input = "before\n// {{#no-redis}}\ncontent\n// {{/no-redis}}\nafter\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "no-redis", include: true);

        Assert.Equal("before\ncontent\nafter\n", result);
    }

    [Fact]
    public void SimilarBlockNames_DoNotInterfere()
    {
        var input = "// {{#redis}}\nredis content\n// {{/redis}}\n// {{#redis-cluster}}\ncluster content\n// {{/redis-cluster}}\n";

        var result = ConditionalBlockProcessor.ProcessBlock(input, "redis", include: false);

        Assert.DoesNotContain("redis content", result);
        Assert.Contains("cluster content", result);
        Assert.Contains("{{#redis-cluster}}", result);
    }
}
