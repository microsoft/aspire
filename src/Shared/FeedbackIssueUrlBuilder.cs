// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Shared;

internal enum FeedbackIssueKind
{
    Bug,
    Idea,
    General
}

internal sealed record FeedbackIssueContext(
    FeedbackIssueKind Kind,
    string? Title = null,
    string? MainText = null,
    string? AspireDoctorOutput = null,
    string? AdditionalContext = null);

internal static class FeedbackIssueUrlBuilder
{
    private const string RepositoryNewIssueUrl = "https://github.com/microsoft/aspire/issues/new";
    private const string RepositoryIssueChooserUrl = "https://github.com/microsoft/aspire/issues/new/choose";
    private const string BugReportTemplate = "10_bug_report.yml";
    private const string FeatureRequestTemplate = "20_feature-request.yml";

    // GitHub rejects requests whose URL exceeds roughly 8 KB (HTTP 414) and silently drops issue
    // prefill above that size, so cap the generated URL well under that limit. The auto-generated
    // diagnostic fields (`aspire doctor` output and the additional-context block) are the only parts
    // that can realistically grow large, so they are the ones trimmed to fit.
    private const int MaxUrlLength = 8000;

    // Appended to a field after it is truncated so the reader knows the captured content was cut to
    // keep the URL under GitHub's limit. The full output is still available from `aspire doctor`.
    private const string TruncationMarker = "\n\n[Truncated to keep the issue URL within GitHub's length limit. Run `aspire doctor` for the full output.]";

    private const string AspireDoctorOutputKey = "aspire-doctor-output";
    private const string AdditionalContextKey = "additional-context";
    private const string BodyKey = "body";

    public static string BuildChooserUrl() => RepositoryIssueChooserUrl;

    public static string BuildUrl(FeedbackIssueContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Kind switch
        {
            FeedbackIssueKind.Bug => BuildBugReportUrl(context),
            FeedbackIssueKind.Idea => BuildFeatureRequestUrl(context),
            FeedbackIssueKind.General => BuildBlankIssueUrl(context),
            _ => throw new ArgumentOutOfRangeException(nameof(context))
        };
    }

    private static string BuildBugReportUrl(FeedbackIssueContext context)
    {
        var query = new Dictionary<string, string?>
        {
            ["template"] = BugReportTemplate,
            ["title"] = context.Title ?? "Aspire bug report",
            ["description"] = context.MainText,
            [AspireDoctorOutputKey] = context.AspireDoctorOutput,
            [AdditionalContextKey] = context.AdditionalContext
        };

        // Trim the doctor output first (largest and most reproducible from the CLI) and only then the
        // additional-context block, so the human-entered title/description are preserved.
        return BuildUrl(query, AspireDoctorOutputKey, AdditionalContextKey);
    }

    private static string BuildFeatureRequestUrl(FeedbackIssueContext context)
    {
        var query = new Dictionary<string, string?>
        {
            ["template"] = FeatureRequestTemplate,
            ["title"] = context.Title ?? "Aspire feature request",
            ["solution"] = context.MainText,
            [AdditionalContextKey] = context.AdditionalContext
        };

        return BuildUrl(query, AdditionalContextKey);
    }

    private static string BuildBlankIssueUrl(FeedbackIssueContext context)
    {
        var query = new Dictionary<string, string?>
        {
            ["title"] = context.Title ?? "Aspire feedback",
            [BodyKey] = BuildBlankIssueBody(context.MainText, context.AdditionalContext)
        };

        return BuildUrl(query, BodyKey);
    }

    private static string? BuildBlankIssueBody(string? mainText, string? additionalContext)
    {
        if (string.IsNullOrWhiteSpace(mainText) && string.IsNullOrWhiteSpace(additionalContext))
        {
            return null;
        }

        var body = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(mainText))
        {
            body.AppendLine(mainText);
        }

        if (!string.IsNullOrWhiteSpace(additionalContext))
        {
            if (body.Length > 0)
            {
                body.AppendLine();
            }

            body.AppendLine("## Additional context");
            body.AppendLine(additionalContext);
        }

        return body.ToString();
    }

    private static string BuildUrl(IReadOnlyDictionary<string, string?> query, params string[] truncatableKeysInTrimOrder)
    {
        var url = Compose(query);
        if (url.Length <= MaxUrlLength)
        {
            return url;
        }

        // The URL is over budget, so rebuild it with the truncatable values trimmed to fit. Everything
        // else (base URL, keys, separators, and the non-truncatable values) is fixed overhead that the
        // truncatable values must share whatever budget remains.
        var trimmed = TrimQueryToBudget(query, truncatableKeysInTrimOrder);
        return Compose(trimmed);
    }

    private static IReadOnlyDictionary<string, string?> TrimQueryToBudget(IReadOnlyDictionary<string, string?> query, string[] truncatableKeysInTrimOrder)
    {
        var truncatable = new HashSet<string>(truncatableKeysInTrimOrder, StringComparer.Ordinal);

        // Fixed overhead = base URL + every present param's separator, escaped key, and '='. Add the
        // escaped values of the non-truncatable params; only the truncatable values can be reduced.
        var fixedOverhead = RepositoryNewIssueUrl.Length;
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Each emitted param contributes "<separator><key>=" before its value.
            fixedOverhead += 1 + Uri.EscapeDataString(key).Length + 1;

            if (!truncatable.Contains(key))
            {
                fixedOverhead += Uri.EscapeDataString(value).Length;
            }
        }

        var budgetForTruncatableValues = MaxUrlLength - fixedOverhead;

        var result = new Dictionary<string, string?>(query, StringComparer.Ordinal);

        // Keep priority is the reverse of trim priority: the value trimmed last keeps its budget first,
        // so the value listed first in trim order ends up with whatever budget is left over.
        var remaining = Math.Max(0, budgetForTruncatableValues);
        for (var i = truncatableKeysInTrimOrder.Length - 1; i >= 0; i--)
        {
            var key = truncatableKeysInTrimOrder[i];
            if (!query.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var fitted = TruncateToEscapedBudget(value, remaining);
            result[key] = fitted;
            remaining -= fitted is null ? 0 : Uri.EscapeDataString(fitted).Length;
        }

        return result;
    }

    /// <summary>
    /// Truncates <paramref name="value"/> (appending <see cref="TruncationMarker"/>) so that its
    /// percent-encoded length does not exceed <paramref name="escapedBudget"/>. Returns the original
    /// value when it already fits, or <see langword="null"/> when not even the marker fits.
    /// </summary>
    private static string? TruncateToEscapedBudget(string value, int escapedBudget)
    {
        if (escapedBudget <= 0)
        {
            return null;
        }

        if (Uri.EscapeDataString(value).Length <= escapedBudget)
        {
            return value;
        }

        if (Uri.EscapeDataString(TruncationMarker).Length > escapedBudget)
        {
            // No room to keep any content and still signal the truncation, so omit the field entirely.
            return null;
        }

        // Binary search for the largest raw prefix whose escaped form (plus the marker) fits the budget.
        // Escaping only ever grows a string, so the escaped length increases monotonically with the cut
        // length, which makes the search well defined.
        var low = 0;
        var high = value.Length;
        var bestCut = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Uri.EscapeDataString(value[..mid] + TruncationMarker).Length <= escapedBudget)
            {
                bestCut = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        // Avoid leaving a dangling high surrogate that would make the cut prefix an invalid string.
        if (bestCut > 0 && char.IsHighSurrogate(value[bestCut - 1]))
        {
            bestCut--;
        }

        return value[..bestCut] + TruncationMarker;
    }

    private static string Compose(IReadOnlyDictionary<string, string?> query)
    {
        var builder = new StringBuilder(RepositoryNewIssueUrl);
        var separator = '?';

        foreach (var (key, value) in query)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.Append(separator);
            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }
}
