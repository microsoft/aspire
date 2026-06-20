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
            ["aspire-doctor-output"] = context.AspireDoctorOutput,
            ["additional-context"] = context.AdditionalContext
        };

        return BuildUrl(query);
    }

    private static string BuildFeatureRequestUrl(FeedbackIssueContext context)
    {
        var query = new Dictionary<string, string?>
        {
            ["template"] = FeatureRequestTemplate,
            ["title"] = context.Title ?? "Aspire feature request",
            ["solution"] = context.MainText,
            ["additional-context"] = context.AdditionalContext
        };

        return BuildUrl(query);
    }

    private static string BuildBlankIssueUrl(FeedbackIssueContext context)
    {
        var query = new Dictionary<string, string?>
        {
            ["title"] = context.Title ?? "Aspire feedback",
            ["body"] = BuildBlankIssueBody(context.MainText, context.AdditionalContext)
        };

        return BuildUrl(query);
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

    private static string BuildUrl(IReadOnlyDictionary<string, string?> query)
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
