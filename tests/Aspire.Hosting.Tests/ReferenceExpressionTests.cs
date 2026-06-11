// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Hosting.Tests;
[Trait("Partition", "5")]
public class ReferenceExpressionTests
{
    [Theory]
    [InlineData("world", "Hello world", "Hello world")]
    [InlineData("{", "Hello {{", "Hello {")]
    [InlineData("}", "Hello }}", "Hello }")]
    [InlineData("{1}", "Hello {{1}}", "Hello {1}")]
    [InlineData("{x}", "Hello {{x}}", "Hello {x}")]
    [InlineData("{{x}}", "Hello {{x}}", "Hello {x}")]
    public void TestReferenceExpressionCreateInputStringTreatedAsLiteral(string input, string expectedFormat, string expectedExpression)
    {
        var refExpression = ReferenceExpression.Create($"Hello {input}");
        Assert.Equal(expectedFormat, refExpression.Format);

        // Generally, the input string should end up unchanged in the expression, since it's a literal
        var expr = refExpression.ValueExpression;
        Assert.Equal(expectedExpression, expr);
    }

    [Theory]
    [InlineData("{x}", "{x}")]
    [InlineData("{x", "{x")]
    [InlineData("x}", "x}")]
    [InlineData("{1 var}", "{1 var}")]
    [InlineData("{var 1}", "{var 1}")]
    [InlineData("{1myVar}", "{1myVar}")]
    [InlineData("{myVar1}", "{myVar1}")]
    public void ReferenceExpressionHandlesValueWithNonParameterBrackets(string input, string expected)
    {
        var expr = ReferenceExpression.Create($"{input}").ValueExpression;
        Assert.Equal(expected, expr);
    }

    [Theory]
    [InlineData("{0}", new string[] { "abc123" }, "abc123")]
    [InlineData("{0} test", new string[] { "abc123" }, "abc123 test")]
    [InlineData("test {0}", new string[] { "abc123" }, "test abc123")]
    [InlineData("https://{0}:{1}/{2}?key={3}", new string[] { "test.com", "443", "path", "1234" }, "https://test.com:443/path?key=1234")]
    public void ReferenceExpressionHandlesValueWithParameterBrackets(string input, string[] parameters, string expected)
    {
        var expr = ReferenceExpression.Create($"{input}", [new HostUrl("test")], parameters, []).ValueExpression;
        Assert.Equal(expected, expr);
    }

    public static readonly object[][] ValidFormattingInParameterBracketCases = [
        ["{0:D}", new DateTime(2024,05,22)],
        ["{0:N}", 123456.78]
    ];

    [Theory, MemberData(nameof(ValidFormattingInParameterBracketCases))]
    public void ReferenceExpressionHandlesValueWithFormattingInParameterBrackets(string input, string parameterValue)
    {
        var expected = string.Format(CultureInfo.InvariantCulture, input, parameterValue);

        var expr = ReferenceExpression.Create($"{input}", [new HostUrl("test")], [parameterValue], []).ValueExpression;
        Assert.Equal(expected, expr);
    }

    [Fact]
    public void ReferenceExpressionHandlesValueWithoutBrackets()
    {
        var s = "Test";
        var expr = ReferenceExpression.Create($"{s}").ValueExpression;
        Assert.Equal("Test", expr);
    }

    [Fact]
    public async Task ReferenceExpressionWithBracketsAndInterpolation()
    {
        var v = new Value();

        var expr = ReferenceExpression.Create($"[{{\"api_uri\":\"{v}\"}}]");

        Assert.Equal("[{\"api_uri\":\"{value}\"}]", expr.ValueExpression);
        Assert.Equal("[{\"api_uri\":\"Hello World\"}]", await expr.GetValueAsync(default));
    }

    [Fact]
    public async Task ReferenceExpressionWithEscapedBracketsAndInterpolation()
    {
        var v = new Value();

        var expr = ReferenceExpression.Create($"[{{{{\"api_uri\":\"{v}\"}}}}]");

        Assert.Equal("[{\"api_uri\":\"{value}\"}]", expr.ValueExpression);
        Assert.Equal("[{\"api_uri\":\"Hello World\"}]", await expr.GetValueAsync(default));
    }

    [Fact]
    public async Task ReferenceExpressionIsUrlEncoded()
    {
        var v = new Value();

        var expr = ReferenceExpression.Create($"Text: {v:uri}");

        Assert.Equal("Text: Hello%20World", await expr.GetValueAsync(default));
    }

    [Fact]
    public async Task ReferenceExpressionBuilderSupportsNullFormat()
    {
        var b = new ReferenceExpressionBuilder();
        b.Append($"Text: ");
        b.AppendFormatted("foo", format: null);

        Assert.Equal("Text: foo", await b.Build().GetValueAsync(default));
    }

    private sealed class Value : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression => "{value}";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            return new("Hello World");
        }
    }

    private sealed class TestCondition(string value) : IValueProvider, IManifestExpressionProvider, IValueWithReferences
    {
        public string ValueExpression => "{test-condition.value}";

        public IEnumerable<object> References => [this];

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) => new(value);
    }

    [Fact]
    public void ConditionalExpression_ValueProviders_ReturnsUnionOfBothBranches()
    {
        var param1 = new Value();
        var param2 = new Value();
        var param3 = new Value();
        var condition = new TestCondition("True");

        var whenTrue = ReferenceExpression.Create($"prefix-{param1}-{param2}");
        var whenFalse = ReferenceExpression.Create($"fallback-{param3}");

        var conditional = ReferenceExpression.CreateConditional(condition, "True", whenTrue, whenFalse);

        Assert.True(conditional.IsConditional);
        Assert.Equal(3, conditional.ValueProviders.Count);
        Assert.Same(param1, conditional.ValueProviders[0]);
        Assert.Same(param2, conditional.ValueProviders[1]);
        Assert.Same(param3, conditional.ValueProviders[2]);
    }

    [Fact]
    public void ConditionalExpression_ValueProviders_EmptyWhenBranchesHaveNoProviders()
    {
        var condition = new TestCondition("True");

        var whenTrue = ReferenceExpression.Create($"literal-a");
        var whenFalse = ReferenceExpression.Create($"literal-b");

        var conditional = ReferenceExpression.CreateConditional(condition, "True", whenTrue, whenFalse);

        Assert.True(conditional.IsConditional);
        Assert.Empty(conditional.ValueProviders);
    }

    [Fact]
    public void ConditionalExpression_References_IncludesConditionAndBothBranches()
    {
        var param1 = new Value();
        var param2 = new Value();
        var condition = new TestCondition("True");

        var whenTrue = ReferenceExpression.Create($"{param1}");
        var whenFalse = ReferenceExpression.Create($"{param2}");

        var conditional = ReferenceExpression.CreateConditional(condition, "True", whenTrue, whenFalse);

        var references = ((IValueWithReferences)conditional).References.ToList();

        // References should include the condition's references, then each branch's references
        Assert.Contains(condition, references);
        Assert.Contains(param1, references);
        Assert.Contains(param2, references);
    }

    [Fact]
    public void NestedConditionalExpression_ValueProviders_IncludesAllNestedProviders()
    {
        var outerCondition = new TestCondition("True");
        var innerCondition = new TestCondition("Yes");
        var param1 = new Value();
        var param2 = new Value();
        var param3 = new Value();

        // Inner conditional: if innerCondition == "Yes" then param1 else param2
        var innerConditional = ReferenceExpression.CreateConditional(
            innerCondition, "Yes",
            ReferenceExpression.Create($"{param1}"),
            ReferenceExpression.Create($"{param2}"));

        // Outer conditional: if outerCondition == "True" then innerConditional else param3
        var outerConditional = ReferenceExpression.CreateConditional(
            outerCondition, "True",
            innerConditional,
            ReferenceExpression.Create($"{param3}"));

        // Outer ValueProviders should be the union of:
        //   innerConditional.ValueProviders (param1, param2) + whenFalse.ValueProviders (param3)
        Assert.Equal(3, outerConditional.ValueProviders.Count);
        Assert.Same(param1, outerConditional.ValueProviders[0]);
        Assert.Same(param2, outerConditional.ValueProviders[1]);
        Assert.Same(param3, outerConditional.ValueProviders[2]);
    }

    [Fact]
    public void NestedConditionalExpression_References_IncludesAllNestedReferences()
    {
        var outerCondition = new TestCondition("True");
        var innerCondition = new TestCondition("Yes");
        var param1 = new Value();
        var param2 = new Value();
        var param3 = new Value();

        var innerConditional = ReferenceExpression.CreateConditional(
            innerCondition, "Yes",
            ReferenceExpression.Create($"{param1}"),
            ReferenceExpression.Create($"{param2}"));

        var outerConditional = ReferenceExpression.CreateConditional(
            outerCondition, "True",
            innerConditional,
            ReferenceExpression.Create($"{param3}"));

        var references = ((IValueWithReferences)outerConditional).References.ToList();

        // Outer condition's references
        Assert.Contains(outerCondition, references);
        // Inner conditional's references (condition + both branches)
        Assert.Contains(innerCondition, references);
        Assert.Contains(param1, references);
        Assert.Contains(param2, references);
        // Outer false branch
        Assert.Contains(param3, references);
    }

    [Fact]
    public void DuplicateConditionalExpressions_HaveSameName()
    {
        var condition = new TestCondition("True");
        var param1 = new Value();
        var param2 = new Value();

        var conditional1 = ReferenceExpression.CreateConditional(
            condition, "True",
            ReferenceExpression.Create($"{param1}"),
            ReferenceExpression.Create($"{param2}"));

        var conditional2 = ReferenceExpression.CreateConditional(
            condition, "True",
            ReferenceExpression.Create($"{param1}"),
            ReferenceExpression.Create($"{param2}"));

        // Two identical conditionals should produce the same hash-based name
        Assert.Equal(conditional1.ValueExpression, conditional2.ValueExpression);
    }
}
