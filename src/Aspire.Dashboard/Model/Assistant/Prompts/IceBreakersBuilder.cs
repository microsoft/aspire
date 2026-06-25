// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;
using Aspire.Dashboard.Components.Deck;

namespace Aspire.Dashboard.Model.Assistant.Prompts;

public sealed class IceBreakersBuilder
{
    private readonly IStringLocalizer<AIPrompts> _loc;
    private readonly InitialPrompt _recentActivity;

    public IceBreakersBuilder(IStringLocalizer<AIPrompts> loc)
    {
        _loc = loc;
        _recentActivity = new InitialPrompt(
            DeckIconName.Clock,
            _loc[nameof(AIPrompts.TitleRecentActivity)],
            _loc[nameof(AIPrompts.PromptTracesAndStructuredLogs)],
            _loc[nameof(AIPrompts.PromptTracesAndStructuredLogs)],
            KnownChatMessages.General.CreateRecentActivityMessage());
    }

    public void ResourceDetails(BuildIceBreakersContext context, ResourceViewModel resource)
    {
        context.Prompts.Add(CreateAnalyzeResource(resource));

        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Gauge,
            _loc[nameof(AIPrompts.TitlePerformance)],
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeResourcePerformance), resource.Name),
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeResourcePerformance), resource.Name),
            KnownChatMessages.Traces.CreateResourceTracesPerformanceMessage(resource.Name)));

        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Clock,
            _loc[nameof(AIPrompts.TitleRecentActivity)],
            _loc.GetString(nameof(AIPrompts.PromptResourceTracesAndStructuredLogs), resource.Name),
            _loc.GetString(nameof(AIPrompts.PromptResourceTracesAndStructuredLogs), resource.Name),
            KnownChatMessages.General.CreateResourceRecentActivityMessage(resource)));
    }

    private InitialPrompt CreateAnalyzeResource(ResourceViewModel resource)
    {
        return new InitialPrompt(
            DeckIconName.Beaker,
            _loc[nameof(AIPrompts.TitleInvestigate)],
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeResource), resource.Name),
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeResource), resource.Name),
            KnownChatMessages.Resources.CreateAnalyzeResourceMessage(resource));
    }

    public void ConsoleLogs(BuildIceBreakersContext context)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Clock,
            _loc[nameof(AIPrompts.TitleRecentConsoleLogs)],
            _loc[nameof(AIPrompts.PromptConsoleLogs)],
            _loc[nameof(AIPrompts.PromptConsoleLogs)],
            KnownChatMessages.ConsoleLogs.CreateAllConsoleLogsMessage()));

        if (context.Prompts.Count < 3)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.Help,
                _loc[nameof(AIPrompts.TitleHelp)],
                _loc[nameof(AIPrompts.PromptHelpConsoleLogs)],
                _loc[nameof(AIPrompts.PromptHelpConsoleLogs)],
                KnownChatMessages.ConsoleLogs.CreateHelpMessage()));
        }
    }

    public void ConsoleLogs(BuildIceBreakersContext context, ResourceViewModel resource)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Clock,
            _loc[nameof(AIPrompts.TitleRecentConsoleLogs)],
            _loc.GetString(nameof(AIPrompts.PromptResourceConsoleLogs), resource.Name),
            _loc.GetString(nameof(AIPrompts.PromptResourceConsoleLogs), resource.Name),
            KnownChatMessages.ConsoleLogs.CreateResourceConsoleLogsMessage(resource)));

        context.Prompts.Add(CreateAnalyzeResource(resource));

        if (context.Prompts.Count < 3)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.Help,
                _loc[nameof(AIPrompts.TitleHelp)],
                _loc[nameof(AIPrompts.PromptHelpConsoleLogs)],
                _loc[nameof(AIPrompts.PromptHelpConsoleLogs)],
                KnownChatMessages.ConsoleLogs.CreateHelpMessage()));
        }
    }

    public void Default(BuildIceBreakersContext context)
    {
        context.Prompts.Add(_recentActivity);
    }

    public void Resources(BuildIceBreakersContext context, bool hasUnhealthyResources)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.DocumentError,
            _loc[nameof(AIPrompts.TitleSummarizeResources)],
            _loc[nameof(AIPrompts.PromptAnalyzeResources)],
            _loc[nameof(AIPrompts.PromptAnalyzeResources)],
            KnownChatMessages.Resources.CreateSummarizeResourcesMessage()));

        if (hasUnhealthyResources)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.BriefcaseMedical,
                _loc[nameof(AIPrompts.TitleInvestigateUnhealthyResources)],
                _loc[nameof(AIPrompts.PromptUnhealthyResources)],
                _loc[nameof(AIPrompts.PromptUnhealthyResources)],
                KnownChatMessages.Resources.CreateUnhealthyResourcesMessage()));
        }

        context.Prompts.Add(_recentActivity);

        if (context.Prompts.Count < 3)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.Help,
                _loc[nameof(AIPrompts.TitleHelp)],
                _loc[nameof(AIPrompts.PromptHelpResources)],
                _loc[nameof(AIPrompts.PromptHelpResources)],
                KnownChatMessages.Resources.CreateHelpMessage()));
        }
    }

    public void StructuredLogs(BuildIceBreakersContext context, Func<PagedResult<OtlpLogEntry>> getCurrentLogs, bool hasErrors, Func<PagedResult<OtlpLogEntry>> getErrorLogs)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Clock,
            _loc[nameof(AIPrompts.TitleRecentActivity)],
            _loc[nameof(AIPrompts.PromptAnalyzeStructuredLogs)],
            _loc[nameof(AIPrompts.PromptAnalyzeStructuredLogs)],
            KnownChatMessages.StructuredLogs.CreateAllStructuredLogsMessage()));

        if (hasErrors)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.BriefcaseMedical,
                _loc[nameof(AIPrompts.TitleExplainErrors)],
                _loc[nameof(AIPrompts.PromptErrorsStructuredLogs)],
                promptContext => PromptContextsBuilder.ErrorStructuredLogs(
                    promptContext,
                    _loc[nameof(AIPrompts.PromptErrorsStructuredLogs)],
                    getErrorLogs)));
        }

        if (context.Prompts.Count < 3)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.Help,
                _loc[nameof(AIPrompts.TitleHelp)],
                _loc[nameof(AIPrompts.PromptHelpStructuredLogs)],
                _loc[nameof(AIPrompts.PromptHelpStructuredLogs)],
                KnownChatMessages.StructuredLogs.CreateHelpMessage()));
        }
    }

    public void StructuredLogs(BuildIceBreakersContext context, OtlpResource resource, Func<PagedResult<OtlpLogEntry>> getCurrentLogs, bool hasErrors, Func<PagedResult<OtlpLogEntry>> getErrorLogs)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Clock,
            _loc[nameof(AIPrompts.TitleRecentActivity)],
            _loc.GetString(nameof(AIPrompts.PromptResourceStructuredLogs), resource.ResourceKey.GetCompositeName()),
            _loc.GetString(nameof(AIPrompts.PromptResourceStructuredLogs), resource.ResourceKey.GetCompositeName()),
            KnownChatMessages.StructuredLogs.CreateResourceStructuredLogsMessage(resource)));

        if (hasErrors)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.BriefcaseMedical,
                _loc[nameof(AIPrompts.TitleExplainErrors)],
                _loc[nameof(AIPrompts.PromptErrorsStructuredLogs)],
                promptContext => PromptContextsBuilder.ErrorStructuredLogs(
                    promptContext,
                    _loc[nameof(AIPrompts.PromptErrorsStructuredLogs)],
                    getErrorLogs)));
        }

        if (context.Prompts.Count < 3)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.Help,
                _loc[nameof(AIPrompts.TitleHelp)],
                _loc[nameof(AIPrompts.PromptHelpStructuredLogs)],
                _loc[nameof(AIPrompts.PromptHelpStructuredLogs)],
                KnownChatMessages.StructuredLogs.CreateHelpMessage()));
        }
    }

    public void StructuredLogs(BuildIceBreakersContext context, OtlpLogEntry logEntry)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Beaker,
            _loc[nameof(AIPrompts.TitleInvestigate)],
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeLogEntry), logEntry.InternalId),
            promptContext => PromptContextsBuilder.AnalyzeLogEntry(
                promptContext,
                _loc.GetString(nameof(AIPrompts.PromptAnalyzeLogEntry), logEntry.InternalId),
                logEntry)));
    }

    public void Traces(BuildIceBreakersContext context, Func<PagedResult<OtlpTrace>> getCurrentTraces, bool hasErrors, Func<PagedResult<OtlpTrace>> getErrorTraces)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Clock,
            _loc[nameof(AIPrompts.TitleRecentActivity)],
            _loc[nameof(AIPrompts.PromptTraces)],
            _loc[nameof(AIPrompts.PromptTraces)],
            KnownChatMessages.Traces.CreateAllTracesMessage()));

        if (hasErrors)
        {
            context.Prompts.Add(CreateErrorTracesPrompt(getErrorTraces));
        }

        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Gauge,
            _loc[nameof(AIPrompts.TitlePerformance)],
            _loc[nameof(AIPrompts.PromptAnalyzePerformance)],
            _loc[nameof(AIPrompts.PromptAnalyzePerformance)],
            KnownChatMessages.Traces.CreateAllTracesPerformanceMessage()));

        if (context.Prompts.Count < 3)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.Help,
                _loc[nameof(AIPrompts.TitleHelp)],
                _loc[nameof(AIPrompts.PromptHelpTraces)],
                _loc[nameof(AIPrompts.PromptHelpTraces)],
                KnownChatMessages.Traces.CreateHelpMessage()));
        }
    }

    private InitialPrompt CreateErrorTracesPrompt(Func<PagedResult<OtlpTrace>> getErrorTraces)
    {
        return new InitialPrompt(
            DeckIconName.BriefcaseMedical,
            _loc[nameof(AIPrompts.TitleExplainErrors)],
            _loc[nameof(AIPrompts.PromptErrorTraces)],
            promptContext => PromptContextsBuilder.ErrorTraces(
                promptContext,
                _loc[nameof(AIPrompts.PromptErrorTraces)],
                getErrorTraces));
    }

    public void Traces(BuildIceBreakersContext context, OtlpResource resource, Func<PagedResult<OtlpTrace>> getCurrentTraces, bool hasErrors, Func<PagedResult<OtlpTrace>> getErrorTraces)
    {
        var resourceName = resource.ResourceKey.GetCompositeName();

        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Clock,
            _loc[nameof(AIPrompts.TitleRecentActivity)],
            _loc.GetString(nameof(AIPrompts.PromptResourceTraces), resourceName),
            _loc.GetString(nameof(AIPrompts.PromptResourceTraces), resourceName),
            KnownChatMessages.Traces.CreateResourceTracesMessage(resource)));

        if (hasErrors)
        {
            context.Prompts.Add(CreateErrorTracesPrompt(getErrorTraces));
        }

        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Gauge,
            _loc[nameof(AIPrompts.TitlePerformance)],
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeResourcePerformance), resourceName),
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeResourcePerformance), resourceName),
            KnownChatMessages.Traces.CreateResourceTracesPerformanceMessage(resourceName)));

        if (context.Prompts.Count < 3)
        {
            context.Prompts.Add(new InitialPrompt(
                DeckIconName.Help,
                _loc[nameof(AIPrompts.TitleHelp)],
                _loc[nameof(AIPrompts.PromptHelpTraces)],
                _loc[nameof(AIPrompts.PromptHelpTraces)],
                KnownChatMessages.Traces.CreateHelpMessage()));
        }
    }

    public void Trace(BuildIceBreakersContext context, OtlpTrace trace)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Beaker,
            _loc[nameof(AIPrompts.TitleInvestigate)],
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeTrace), OtlpHelpers.ToShortenedId(trace.TraceId)),
            context =>
            {
                return PromptContextsBuilder.AnalyzeTrace(
                    context,
                    _loc.GetString(nameof(AIPrompts.PromptAnalyzeTrace), OtlpHelpers.ToShortenedId(trace.TraceId)),
                    trace);
            }));
    }

    public void Span(BuildIceBreakersContext context, OtlpSpan span)
    {
        context.Prompts.Add(new InitialPrompt(
            DeckIconName.Beaker,
            _loc[nameof(AIPrompts.TitleInvestigate)],
            _loc.GetString(nameof(AIPrompts.PromptAnalyzeSpan), OtlpHelpers.ToShortenedId(span.SpanId)),
            context =>
            {
                return PromptContextsBuilder.AnalyzeSpan(
                    context,
                    _loc.GetString(nameof(AIPrompts.PromptAnalyzeSpan), OtlpHelpers.ToShortenedId(span.SpanId)),
                    span);
            }));
    }
}
