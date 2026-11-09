// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

public enum DashboardFeedbackUsageDuration
{
    NotSpecified,
    LessThanOneHour,
    OneDay,
    TwoToSevenDays,
    MoreThanOneWeek
}

public enum DashboardFeedbackReason
{
    Navigation,
    FindingApplicationUrls,
    TelemetryWorkflow,
    Terminals,
    PerformanceOrAccessibility,
    MissingFeature,
    Other
}

public sealed class DashboardFeedback
{
    public DashboardFeedbackUsageDuration UsageDuration { get; set; }
    public HashSet<DashboardFeedbackReason> Reasons { get; } = [];
    public string? OtherReason { get; set; }
    public string? MissingCapabilities { get; set; }
    public string? Difficulties { get; set; }
    public required bool IsAppHost { get; init; }
    public Func<Task>? SwitchToLegacyAsync { get; init; }

    internal string BuildMarkdown(IStringLocalizer<Resources.Dialogs> loc)
    {
        var markdown = new StringBuilder();
        markdown.Append("### ").AppendLine(loc[nameof(Resources.Dialogs.DashboardFeedbackMarkdownTitle)]);

        if (UsageDuration != DashboardFeedbackUsageDuration.NotSpecified)
        {
            AppendSection(
                markdown,
                loc[nameof(Resources.Dialogs.DashboardFeedbackDurationLabel)],
                GetUsageDurationText(UsageDuration, loc));
        }

        if (Reasons.Count > 0)
        {
            markdown.AppendLine();
            markdown.Append("**")
                .Append(loc[nameof(Resources.Dialogs.DashboardFeedbackReasonsLabel)])
                .AppendLine("**");
            foreach (var reason in Enum.GetValues<DashboardFeedbackReason>().Where(Reasons.Contains))
            {
                var reasonText = GetReasonText(reason, loc);
                if (reason == DashboardFeedbackReason.Other && !string.IsNullOrWhiteSpace(OtherReason))
                {
                    reasonText = $"{reasonText}: {OtherReason.Trim()}";
                }

                markdown.Append("- ").AppendLine(reasonText);
            }
        }

        AppendSectionIfPresent(
            markdown,
            loc[nameof(Resources.Dialogs.DashboardFeedbackMissingLabel)],
            MissingCapabilities);
        AppendSectionIfPresent(
            markdown,
            loc[nameof(Resources.Dialogs.DashboardFeedbackDifficultiesLabel)],
            Difficulties);
        AppendSection(
            markdown,
            loc[nameof(Resources.Dialogs.DashboardFeedbackModeLabel)],
            loc[IsAppHost
                ? nameof(Resources.Dialogs.DashboardFeedbackModeAppHost)
                : nameof(Resources.Dialogs.DashboardFeedbackModeStandalone)]);

        return markdown.ToString().TrimEnd();
    }

    private static void AppendSectionIfPresent(StringBuilder markdown, string heading, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            AppendSection(markdown, heading, value.Trim());
        }
    }

    private static void AppendSection(StringBuilder markdown, string heading, string value)
    {
        markdown.AppendLine();
        markdown.Append("**").Append(heading).AppendLine("**");
        markdown.AppendLine(value);
    }

    private static string GetUsageDurationText(
        DashboardFeedbackUsageDuration duration,
        IStringLocalizer<Resources.Dialogs> loc) => duration switch
        {
            DashboardFeedbackUsageDuration.LessThanOneHour => loc[nameof(Resources.Dialogs.DashboardFeedbackDurationLessThanOneHour)],
            DashboardFeedbackUsageDuration.OneDay => loc[nameof(Resources.Dialogs.DashboardFeedbackDurationOneDay)],
            DashboardFeedbackUsageDuration.TwoToSevenDays => loc[nameof(Resources.Dialogs.DashboardFeedbackDurationTwoToSevenDays)],
            DashboardFeedbackUsageDuration.MoreThanOneWeek => loc[nameof(Resources.Dialogs.DashboardFeedbackDurationMoreThanOneWeek)],
            _ => throw new ArgumentOutOfRangeException(nameof(duration))
        };

    private static string GetReasonText(
        DashboardFeedbackReason reason,
        IStringLocalizer<Resources.Dialogs> loc) => reason switch
        {
            DashboardFeedbackReason.Navigation => loc[nameof(Resources.Dialogs.DashboardFeedbackReasonNavigation)],
            DashboardFeedbackReason.FindingApplicationUrls => loc[nameof(Resources.Dialogs.DashboardFeedbackReasonFindingApplicationUrls)],
            DashboardFeedbackReason.TelemetryWorkflow => loc[nameof(Resources.Dialogs.DashboardFeedbackReasonTelemetryWorkflow)],
            DashboardFeedbackReason.Terminals => loc[nameof(Resources.Dialogs.DashboardFeedbackReasonTerminals)],
            DashboardFeedbackReason.PerformanceOrAccessibility => loc[nameof(Resources.Dialogs.DashboardFeedbackReasonPerformanceOrAccessibility)],
            DashboardFeedbackReason.MissingFeature => loc[nameof(Resources.Dialogs.DashboardFeedbackReasonMissingFeature)],
            DashboardFeedbackReason.Other => loc[nameof(Resources.Dialogs.DashboardFeedbackReasonOther)],
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
}
