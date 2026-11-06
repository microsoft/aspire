// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks diagnostic redaction, Unicode limits and inert rendering of untrusted text.</summary>
public sealed class AnalyzeCiFailureTextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongNonSecretPrefixesAreSanitizedWithinTheProcessingBudget(bool metadata)
    {
        var text = string.Concat(Enumerable.Repeat("a-", 10000));
        _ = AnalyzeCiFailure.DiagnosticTextSanitizer.Sanitize("warmup");
        var started = Stopwatch.GetTimestamp();

        var sanitized = metadata
            ? AnalyzeCiFailure.DiagnosticTextSanitizer.SanitizeRunMetadata(text, limit: 4000)
            : AnalyzeCiFailure.DiagnosticTextSanitizer.Sanitize(text, limit: 4000);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(string.Concat(Enumerable.Repeat("a-", 2000)), sanitized);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Sanitizing 20,000 characters took {elapsed}.");
    }

    [Theory]
    [InlineData("{prefix}token=secret", "{prefix}token=[REDACTED]")]
    [InlineData("{prefix}token='secret'", "{prefix}token='[REDACTED]'")]
    [InlineData("{prefix}token=\"secret\"", "{prefix}token=\"[REDACTED]\"")]
    [InlineData("\"{prefix}token\": \"secret\"", "\"{prefix}token\": \"[REDACTED]\"")]
    [InlineData("'{prefix}token': 'secret'", "'{prefix}token': '[REDACTED]'")]
    [InlineData("{prefix}ConnectionString=Server=db;Password=secret", "{prefix}ConnectionString=[REDACTED]")]
    [InlineData("{prefix}ConnectionString='Server=db;Password=secret'", "{prefix}ConnectionString='[REDACTED]'")]
    [InlineData("{prefix}ConnectionStrings__db=\"Server=db;Password=secret\"", "{prefix}ConnectionStrings__db=\"[REDACTED]\"")]
    [InlineData("{prefix}ConnectionStrings__db=Server=db;Password=secret", "{prefix}ConnectionStrings__db=[REDACTED]")]
    public void LongSettingPrefixesDoNotWeakenSecretRedaction(string input, string expected)
    {
        var prefix = new string('a', 500) + "_";
        input = input.Replace("{prefix}", prefix, StringComparison.Ordinal);
        expected = expected.Replace("{prefix}", prefix, StringComparison.Ordinal);

        Assert.Equal(expected, AnalyzeCiFailure.DiagnosticTextSanitizer.Sanitize(input));
        Assert.Equal(expected, AnalyzeCiFailure.DiagnosticTextSanitizer.SanitizeRunMetadata(input, int.MaxValue));
    }

    [Fact]
    public void FocusedLogsPrioritizeActualAssertionsOverPassingTestsAndGenericNoise()
    {
        var noise = string.Join('\n', Enumerable.Range(1, count: 250).Select(index =>
            $"2026-10-29T01:00:00Z   \u2714 passing test {index} handles failed operations\nordinary failed operation {index}"));
        var focused = AnalyzeCiFailure.WorkflowRunEvidenceCollector.FocusLogs(
            $"AssertionError: early failure\n{noise}\nAssertionError [ERR_ASSERTION]: Lock holder failed: signal=SIGTERM\nnull !== 0\n##[error]Process completed with exit code 1.\n");

        Assert.Contains("AssertionError: early failure", focused, StringComparison.Ordinal);
        Assert.Contains("AssertionError [ERR_ASSERTION]: Lock holder failed: signal=SIGTERM", focused, StringComparison.Ordinal);
        Assert.Contains("null !== 0", focused, StringComparison.Ordinal);
        Assert.True(focused.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 150);
    }

    [Theory]
    [InlineData("AssertionError [ERR_ASSERTION]: web entered FailedToStart")]
    [InlineData("AssertionError: web entered FailedToStart")]
    public void FocusedLogsRetainNodeAssertionBeforeTheFinalExitAnnotation(string assertion)
    {
        var log = $"""
            setup
            before assertion
            2026-10-20T04:02:02Z {assertion}
                at file:///workspace/NuxtApp/verify.mjs:19:20
            generatedMessage: false
            context six
            context seven
            context eight
            omitted gap
            final context ten
            final context eleven
            final context twelve
            ##[error]Process completed with exit code 1.
            cleanup
            """.ReplaceLineEndings("\n");

        var focused = AnalyzeCiFailure.WorkflowRunEvidenceCollector.FocusLogs(log);

        Assert.Equal(
            $"""
            1-setup
            2-before assertion
            3:2026-10-20T04:02:02Z {assertion}
            4-    at file:///workspace/NuxtApp/verify.mjs:19:20
            5-generatedMessage: false
            6-context six
            7-context seven
            8-context eight
            --
            10-final context ten
            11-final context eleven
            12-final context twelve
            13:##[error]Process completed with exit code 1.
            14-cleanup
            """.ReplaceLineEndings("\n") + "\n",
            focused);
    }

    [Theory]
    [InlineData("Fix ConnectionString: preserve named configuration", "Fix ConnectionString: [REDACTED]", "Fix ConnectionString: preserve named configuration")]
    [InlineData("ConnectionString=Server=db;Password=secret", "ConnectionString=[REDACTED]", "ConnectionString=[REDACTED]")]
    [InlineData("ConnectionStrings__db='Server=db;Password=secret'", "ConnectionStrings__db='[REDACTED]'", "ConnectionStrings__db='[REDACTED]'")]
    [InlineData("\"ConnectionStrings\": \"Server=db\"", "\"ConnectionStrings\": \"[REDACTED]\"", "\"ConnectionStrings\": \"[REDACTED]\"")]
    public void DiagnosticTextAndRunMetadataPreserveTheirConnectionStringPolicies(string input, string diagnostic, string metadata)
    {
        Assert.Equal(diagnostic, AnalyzeCiFailure.DiagnosticTextSanitizer.Sanitize(input));
        Assert.Equal(metadata, AnalyzeCiFailure.DiagnosticTextSanitizer.SanitizeRunMetadata(input, int.MaxValue));
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("null", "null")]
    [InlineData("\"\"", "")]
    [InlineData("\"null\"", "null")]
    [InlineData("\"line\\n\\\"quoted\\\"\\\\tail\"", "line\n\"quoted\"\\tail")]
    [InlineData("\"\\u003cT\\u003e\"", "<T>")]
    [InlineData("\"\\uD83D\\uDE00\"", "\U0001f600")]
    [InlineData("10.0", "10.0")]
    [InlineData("1e1", "1e1")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("123.000000000000000001", "123.000000000000000001")]
    [InlineData("1e-400", "1e-400")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("""[1,"text",false,null]""", """[1,"text",false,null]""")]
    [InlineData("""{ "x": [1, true, null], "s": "text" }""", """{"x":[1,true,null],"s":"text"}""")]
    public void ValueTextDecodesStringsAndPreservesOtherJsonRepresentations(string? json, string expected)
    {
        var node = json is null ? null : AnalyzeCiFailure.CiFailureJson.Parse(json);

        Assert.Equal(expected, AnalyzeCiFailure.CiFailureJson.GetValueText(node));
    }

    [Fact]
    public void ValueTextPreservesTheJsonRepresentationOfNonStringBackedValues()
    {
        var node = JsonValue.Create('x');

        Assert.Equal("\"x\"", AnalyzeCiFailure.CiFailureJson.GetValueText(node));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("null", "")]
    [InlineData("false", "")]
    [InlineData("\"false\"", "false")]
    [InlineData("\"null\"", "null")]
    [InlineData("\"\"", "")]
    [InlineData("\"value\\u0000\\n\"", "value\0\n")]
    [InlineData("true", "true")]
    [InlineData("0", "0")]
    [InlineData("1e1", "1e1")]
    [InlineData("[]", "[]")]
    [InlineData("""{"value":false}""", """{"value":false}""")]
    public void RequiredReasonTextRejectsMissingNullAndNonStringValues(string? json, string expected)
    {
        var value = json is null ? null : JsonNode.Parse(json);
        var document = new JsonObject { ["job_id"] = "123", ["classification"] = "code-issue" };
        if (json is not null)
        {
            document["reason"] = value?.DeepClone();
        }
        if (value?.GetValueKind() == JsonValueKind.String)
        {
            Assert.Equal(expected, AnalyzeCiFailure.TransientJson.Read<AnalyzeCiFailure.ProposedJobDecision>(document.ToJsonString()).Reason);
        }
        else
        {
            Assert.Throws<AnalyzeCiFailure.ValidationException>(() =>
                AnalyzeCiFailure.TransientJson.Read<AnalyzeCiFailure.ProposedJobDecision>(document.ToJsonString()));
        }
    }

    [Theory]
    [InlineData("main\0\n\n", "main")]
    [InlineData("ma\0in", "main")]
    [InlineData("\0flaky-test\n", "flaky-test")]
    [InlineData("n\0ull\n", "null")]
    [InlineData(" main \n", " main ")]
    [InlineData("main\r\n", "main\r")]
    [InlineData("main\t", "main\t")]
    [InlineData("main\nmiddle\n", "main\nmiddle")]
    [InlineData("", "")]
    public void IdentityFieldsDoNotStripControlsOrCoerceNullText(string text, string formerlyNormalized)
    {
        var node = JsonValue.Create(text);

        Assert.Equal(text, AnalyzeCiFailure.CiFailureJson.GetValueText(node));
        if (formerlyNormalized == "null")
        {
            Assert.Throws<AnalyzeCiFailure.ValidationException>(() => AnalyzeCiFailure.TransientJson.Read<AnalyzeCiFailure.CollectedSubjectPr>(
                new JsonObject { ["number"] = "42", ["metadata"] = text }.ToJsonString()));
        }
        else
        {
            Assert.False(formerlyNormalized == "flaky-test"
                ? AnalyzeCiFailure.CiFailureJson.TryReadEnum<AnalyzeCiFailure.AnalysisVerdict>(text, out _)
                : AnalyzeCiFailure.CiFailureJson.TryReadEnum<AnalyzeCiFailure.RunScope>(text, out _));
        }
    }

    [Fact]
    public void DurableValueTextTranslatesMalformedUnicode()
    {
        var node = JsonNode.Parse("\"bad\\ud800\"");

        var exception = Assert.Throws<JsonException>(() => AnalyzeCiFailure.CiFailureJson.GetValueText(node));

        Assert.Equal("JSON contains malformed Unicode text", exception.Message);
    }

    [Theory]
    [InlineData(500, 500, true)]
    [InlineData(501, 500, false)]
    [InlineData(238, 238, true)]
    [InlineData(239, 238, false)]
    public void TextLimitsCountUnicodeCodePoints(int count, int limit, bool expected)
    {
        var text = string.Concat(Enumerable.Repeat("\U0001f600", count));

        Assert.Equal(expected, AnalyzeCiFailure.CiFailureJson.Safe(JsonValue.Create(text), limit));
    }

    [Theory]
    [InlineData("\u0001")]
    [InlineData("\u200b")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\ufe00")]
    [InlineData("\U000e0041")]
    [InlineData("\U000e0100")]
    [InlineData("\U000e01ef")]
    public void RawTextRuleRejectsInvisibleCodePoints(string character)
    {
        // Entry sanitization strips these characters. Exercise the independent rule before
        // sanitization to catch supplementary-plane format/variation-selector regressions.
        var text = JsonValue.Create("before" + character + "after");

        Assert.False(AnalyzeCiFailure.CiFailureJson.Safe(text, max: 500));
        Assert.False(AnalyzeCiFailure.CiFailureJson.Safe(text, max: 500, multiline: true));
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    public void OnlyMultilineFieldsAllowTabAndNewline(string character)
    {
        var text = JsonValue.Create("before" + character + "after");

        Assert.False(AnalyzeCiFailure.CiFailureJson.Safe(text, max: 500));
        Assert.True(AnalyzeCiFailure.CiFailureJson.Safe(text, max: 500, multiline: true));
    }
}
