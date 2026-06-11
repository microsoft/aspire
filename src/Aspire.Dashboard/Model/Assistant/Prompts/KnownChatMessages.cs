// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.AI;

namespace Aspire.Dashboard.Model.Assistant.Prompts;

internal static class KnownChatMessages
{
    public static class General
    {
        public static ChatMessage CreateSystemMessage()
        {
            var locale = GlobalizationHelpers.TryGetKnownParentCulture(CultureInfo.CurrentUICulture, out var matchedCulture)
                ? matchedCulture.Name
                : CultureInfo.CurrentUICulture.Name;

            var systemChatMessage =
                $"""
                You are an AI debugging assistant for apps made using Aspire.
                When asked for your name, you must respond with "GitHub Copilot".
                Follow the user's requirements carefully & to the letter.
                Your expertise is strictly limited to software development topics.
                Follow Microsoft content policies.
                Avoid content that violates copyrights.
                For questions not related to software development, simply give a reminder that you are an AI debugging assistant.
                Respond in the following locale: {locale}
                Include emojis in section titles.

                You MUST plan extensively before each function call, and reflect extensively on the outcomes of the previous function calls. DO NOT do this entire process by making function calls only, as this can impair your ability to solve the problem and think insightfully.

                Respond in Markdown. Use language-specific markdown code fences for multi-line code.
                Ensure your response is short, impersonal, expertly written and easy to understand.

                Resource names, log_id, trace_id and span_id should be formatted as code.
                """;

            return new ChatMessage(ChatRole.System, systemChatMessage);
        }

        public static ChatMessage CreateInitialMessage(string promptText, string applicationName, List<ResourceViewModel> resources, DashboardOptions dashboardOptions)
        {
            var resourceGraph = AIHelpers.GetResponseGraphJson(resources, dashboardOptions);

            var resolvedPromptText =
                $"""
                # USER QUESTION

                {promptText}

                # APP CONTEXT

                Aspire application name: {applicationName}

                # RESOURCE GRAPH

                Always format resource_name in the response as code like this: `frontend-abcxyz`
                Console logs for a resource can provide more information about why a resource is not in a running state.

                {resourceGraph}
                """;
            return new(ChatRole.User, resolvedPromptText);
        }

        public static ChatMessage CreateFollowUpMessage(int questionCount)
        {
            var prompt =
                $"""
                Write a list of {questionCount} questions that I can ask that naturally follows from the previous few questions and answers.
                It should not ask a question which is already answered in the conversation. It should be a question that you are capable of answering.
                Reply with only the list of the questions and nothing else.
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateRecentActivityMessage()
        {
            var prompt =
                """
                Summarize recent traces and structured logs for all resources.
                Investigate the root cause of any errors in traces or structured logs.
                """;

            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateResourceRecentActivityMessage(ResourceViewModel resource)
        {
            var message =
                $"""
                Summarize recent traces and structured logs for `{resource.Name}` resource.
                Investigate the root cause of any errors in traces or structured logs.
                """;
            return new(ChatRole.User, message);
        }
    }

    public static class Resources
    {
        public static ChatMessage CreateAnalyzeResourceMessage(ResourceViewModel resource)
        {
            var prompt =
                $"""
                Investigate resource `{resource.Name}`. Consider whether the resource is running and healthy and it's architectural purpose in the app.
                If the resource isn't running then get console logs to find the root cause.
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateSummarizeResourcesMessage()
        {
            var prompt =
                """
                My application has resources that represent .NET projects, containers, executables, and custom resources.
                Using a couple of bullet points, and up to 300 words, summarize what the state of my application is.
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateUnhealthyResourcesMessage()
        {
            var prompt =
                """
                My application has resources that represent .NET projects, containers, executables, and custom resources.
                Investigate the state and health of resources. Focus on resources that aren't running or healthy.
                Get console logs for resources that aren't running or healthy to find the root cause.
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateHelpMessage()
        {
            var prompt =
                """
                What are Aspire resources? How do they work? What are the different types of resources? How do I use them?
                """;
            return new(ChatRole.User, prompt);
        }
    }

    public static class ConsoleLogs
    {
        public static ChatMessage CreateAllConsoleLogsMessage()
        {
            var prompt =
                """
                Summarize recent console logs for all resources.
                Investigate the root cause of any errors in console logs.
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateResourceConsoleLogsMessage(ResourceViewModel resource)
        {
            var prompt =
                $"""
                Summarize recent console logs for `{resource.Name}`.
                Investigate the root cause of any errors in console logs.
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateHelpMessage()
        {
            var prompt =
                """
                What are console logs? How do they work? What are the different types of console logs? How do I use them?
                """;
            return new(ChatRole.User, prompt);
        }
    }

    public static class StructuredLogs
    {
        public static ChatMessage CreateErrorStructuredLogsMessage(List<OtlpLogEntry> errorLogs, DashboardOptions options, Func<IOtlpResource, string> getResourceName)
        {
            var otlpData = TelemetryExportService.ConvertLogsToOtlpJson(errorLogs);
            var (logsData, limitMessage) = AIHelpers.GetStructuredLogsJson(otlpData, options, getResourceName);

            var prompt =
                $"""
                Explain the errors in the following log entries. Provide a summary of the errors and their possible causes.

                Always format log_id in the response as code like this: `log_id: 123`.
                {limitMessage}

                # STRUCTURED LOGS DATA

                {logsData}
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateAnalyzeLogEntryMessage(OtlpLogEntry logEntry, DashboardOptions options, Func<IOtlpResource, string> getResourceName)
        {
            var otlpData = TelemetryExportService.ConvertLogsToOtlpJson([logEntry]);

            var prompt =
                $"""
                My application has written a log entry. Provide context about the state of the app when the log entry was written and why.
                Response should be a couple of bullet points, and up to 150 words.
                Investigate the root cause of any errors in the log entry.

                # LOG ENTRY DATA

                {AIHelpers.GetStructuredLogJson(otlpData, options, getResourceName)}
                """;

            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateResourceStructuredLogsMessage(OtlpResource resource)
        {
            var prompt =
                $"""
                Summarize recent structured logs for `{resource.ResourceKey.GetCompositeName()}`.
                Investigate the root cause of any errors in structured logs.
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateAllStructuredLogsMessage()
        {
            var prompt =
                """
                Summarize recent structured logs for all resources.
                Investigate the root cause of any errors in structured logs.
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateHelpMessage()
        {
            var prompt =
                """
                What are structured logs? How do they work? What are the different types of structured logs? How do I use them?
                """;
            return new(ChatRole.User, prompt);
        }
    }

    public static class Traces
    {
        public static ChatMessage CreateAllTracesPerformanceMessage()
        {
            var message =
                """
                Analyze recent traces for all resource for performance issues. Include details about traces that have a long duration.
                """;
            return new(ChatRole.User, message);
        }

        public static ChatMessage CreateResourceTracesPerformanceMessage(string resourceName)
        {
            var message =
                $"""
                Analyze recent traces for `{resourceName}` resource for performance issues. Include details about traces that have a long duration.
                """;
            return new(ChatRole.User, message);
        }

        public static ChatMessage CreateAllTracesMessage()
        {
            var message =
                $"""
                "Summarize recent distributed traces for all resources. Focus on distributed traces that report errors."
                """;

            return new ChatMessage(ChatRole.User, message);
        }

        public static ChatMessage CreateResourceTracesMessage(OtlpResource resource)
        {
            var message =
                $"""
                Summarize recent distributed traces for `{resource.ResourceKey.GetCompositeName()}`. Focus on distributed traces that report errors.
                """;

            return new ChatMessage(ChatRole.User, message);
        }

        public static ChatMessage CreateAnalyzeTraceMessage(OtlpTrace trace, List<OtlpLogEntry> traceLogEntries, IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers, DashboardOptions options, Func<IOtlpResource, string> getResourceName)
        {
            var spans = TelemetryExportService.ConvertTracesToOtlpJson([trace], outgoingPeerResolvers.ToArray()).ResourceSpans;
            var (tracesData, _) = SharedAIHelpers.GetTracesJson(spans, getResourceName, AIHelpers.GetDashboardUrl(options));

            var otlpData = TelemetryExportService.ConvertLogsToOtlpJson(traceLogEntries);
            var (logsData, limitMessage) = AIHelpers.GetStructuredLogsJson(otlpData, options, getResourceName);

            var prompt =
                $"""
                My application has written a distributed trace with trace_id `{OtlpHelpers.ToShortenedId(trace.TraceId)}`.
                Summarize the distributed trace. Focus on errors.

                # DISTRIBUTED TRACE DATA

                {tracesData}
                
                # STRUCTURED LOGS DATA

                {limitMessage}

                {logsData}
                """;

            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateAnalyzeSpanMessage(OtlpSpan span, List<OtlpLogEntry> traceLogEntries, IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers, DashboardOptions options, Func<IOtlpResource, string> getResourceName)
        {
            var spans = TelemetryExportService.ConvertTracesToOtlpJson([span.Trace], outgoingPeerResolvers.ToArray()).ResourceSpans;
            var (tracesData, _) = SharedAIHelpers.GetTracesJson(spans, getResourceName, AIHelpers.GetDashboardUrl(options));

            var otlpData = TelemetryExportService.ConvertLogsToOtlpJson(traceLogEntries);
            var (logsData, limitMessage) = AIHelpers.GetStructuredLogsJson(otlpData, options, getResourceName);

            var prompt =
                $"""
                My application has written a distributed trace with trace_id `{OtlpHelpers.ToShortenedId(span.TraceId)}`.
                Summarize the distributed span `{OtlpHelpers.ToShortenedId(span.SpanId)}`. Focus on errors.

                # DISTRIBUTED TRACE DATA

                {tracesData}
                
                # STRUCTURED LOGS DATA
                
                {limitMessage}
                
                {logsData}
                """;

            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateErrorTracesMessage(List<OtlpTrace> errorTraces, IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers, DashboardOptions options, Func<IOtlpResource, string> getResourceName)
        {
            var spans = TelemetryExportService.ConvertTracesToOtlpJson(errorTraces, outgoingPeerResolvers.ToArray()).ResourceSpans;
            var (tracesData, limitMessage) = SharedAIHelpers.GetTracesJson(spans, getResourceName, AIHelpers.GetDashboardUrl(options));

            var prompt =
                $"""
                Explain the errors in the following distributed traces. Provide a summary of the errors and their possible causes.

                {limitMessage}

                # DISTRIBUTED TRACES DATA

                {tracesData}
                """;
            return new(ChatRole.User, prompt);
        }

        public static ChatMessage CreateHelpMessage()
        {
            var prompt =
                """
                What are distributed traces? How do they work? What are the different types of distributed traces? How do I use them?
                """;
            return new(ChatRole.User, prompt);
        }
    }
}
