// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;

namespace Aspire.TestTools;

public static class MochaReader
{
    public static MochaReport Read(string filePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("failures", out var failures) || failures.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Test result does not have the expected Mocha reporter shape");
            }

            // extension/scripts/e2e-mocha-reporter.cjs emits:
            // {"tests":[{"fullTitle":"suite test"}],
            //  "failures":[{"fullTitle":"suite test","err":{"name":"AssertionError","message":"...","stack":"..."}}]}
            // Hook failures may not appear in completed tests. Preserve them so callers
            // can distinguish a broken harness from a completed test assertion.
            var completed = tests.EnumerateArray().Select(ReadName).ToImmutableArray();
            var failed = failures.EnumerateArray().Select(failure =>
            {
                var name = ReadName(failure);
                if (name.Length == 0 || !failure.TryGetProperty("err", out var error) || error.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Mocha failure has an invalid shape");
                }

                return new MochaFailure(
                    name,
                    OptionalText(error, "name"),
                    OptionalText(error, "message") ?? "",
                    OptionalText(error, "stack") ?? "");
            }).ToImmutableArray();

            return new MochaReport(completed, failed);
        }
        catch (InvalidOperationException ex)
        {
            throw new JsonException("Mocha report contains malformed Unicode text", ex);
        }
    }

    private static string ReadName(JsonElement test)
    {
        if (test.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Mocha test has an invalid shape");
        }
        var title = test.TryGetProperty("fullTitle", out var fullTitle) &&
            fullTitle.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
                ? fullTitle
                : test.TryGetProperty("title", out var fallback) ? fallback : default;
        if (title.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Mocha test name must be a string");
        }

        return title.GetString()!;
    }

    private static string? OptionalText(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (field.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Mocha {property} must be a string");
        }

        return field.GetString();
    }
}

public sealed record MochaReport(ImmutableArray<string> CompletedTests, ImmutableArray<MochaFailure> Failures);
public sealed record MochaFailure(string Name, string? ErrorName, string ErrorMessage, string StackTrace);
