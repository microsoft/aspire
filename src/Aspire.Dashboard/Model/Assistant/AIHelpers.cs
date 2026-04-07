// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Otlp.Serialization;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model.Assistant;

internal static class AIHelpers
{
    public const int TracesLimit = 200;
    public const int StructuredLogsLimit = 200;
    public const int ConsoleLogsLimit = 500;

    // There is currently a 64K token limit in VS.
    // Limit the result from individual token calls to a smaller number so multiple results can live inside the context.
    public const int MaximumListTokenLength = 8192;

    // This value is chosen to balance:
    // - Providing enough data to the model for it to provide accurate answers.
    // - Providing too much data and exceeding length limits.
    public const int MaximumStringLength = 2048;

    // Always pass English translations to AI
    private static readonly IStringLocalizer<Columns> s_columnsLoc = new InvariantStringLocalizer<Columns>();

    public static readonly TimeSpan ResponseMessageTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CompleteMessageTimeout = TimeSpan.FromMinutes(4);

    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static string GetResponseGraphJson(List<ResourceViewModel> resources, DashboardOptions options, bool includeDashboardUrl = false, Func<ResourceViewModel, string>? getResourceName = null, bool includeEnvironmentVariables = false)
    {
        var dashboardBaseUrl = includeDashboardUrl ? GetDashboardUrl(options) : null;
        return GetResponseGraphJson(resources, dashboardBaseUrl, includeDashboardUrl, getResourceName, includeEnvironmentVariables);
    }

    internal static string GetResponseGraphJson(List<ResourceViewModel> resources, string? dashboardBaseUrl, bool includeDashboardUrl = false, Func<ResourceViewModel, string>? getResourceName = null, bool includeEnvironmentVariables = false)
    {
        var dataArray = new JsonArray();

        foreach (var resource in resources.Where(resource => !resource.IsResourceHidden(false)))
        {
            var resourceName = getResourceName?.Invoke(resource) ?? resource.Name;

            var endpointUrlsArray = new JsonArray();
            foreach (var u in resource.Urls.Where(u => !u.IsInternal))
            {
                var urlObj = new JsonObject
                {
                    ["name"] = u.EndpointName,
                    ["url"] = u.Url.ToString()
                };
                if (!string.IsNullOrEmpty(u.DisplayProperties.DisplayName))
                {
                    urlObj["display_name"] = u.DisplayProperties.DisplayName;
                }
                endpointUrlsArray.Add(urlObj);
            }

            var healthReportsArray = new JsonArray();
            foreach (var report in resource.HealthReports)
            {
                healthReportsArray.Add(new JsonObject
                {
                    ["name"] = report.Name,
                    ["health_status"] = GetReportHealthStatus(resource, report),
                    ["exception"] = report.ExceptionText
                });
            }

            var healthObj = new JsonObject
            {
                ["resource_health_status"] = GetResourceHealthStatus(resource),
                ["health_reports"] = healthReportsArray
            };

            var commandsArray = new JsonArray();
            foreach (var cmd in resource.Commands.Where(cmd => cmd.State == CommandViewModelState.Enabled))
            {
                commandsArray.Add(new JsonObject
                {
                    ["name"] = cmd.Name,
                    ["description"] = cmd.GetDisplayDescription()
                });
            }

            var resourceObj = new JsonObject
            {
                ["resource_name"] = resourceName,
                ["type"] = resource.ResourceType,
                ["state"] = resource.State,
                ["state_description"] = ResourceStateViewModel.GetResourceStateTooltip(resource, s_columnsLoc),
                ["relationships"] = GetResourceRelationshipsJson(resources, resource, getResourceName),
                ["endpoint_urls"] = endpointUrlsArray,
                ["health"] = healthObj,
                ["source"] = ResourceSourceViewModel.GetSourceViewModel(resource)?.Value,
                ["commands"] = commandsArray
            };

            if (includeDashboardUrl && dashboardBaseUrl != null)
            {
                resourceObj["dashboard_link"] = SharedAIHelpers.GetDashboardLinkObject(dashboardBaseUrl, DashboardUrls.ResourcesUrl(resource: resource.Name), resourceName);
            }

            if (includeEnvironmentVariables)
            {
                var envVarsArray = new JsonArray();
                foreach (var e in resource.Environment.Where(e => e.FromSpec))
                {
                    envVarsArray.Add(JsonValue.Create(e.Name));
                }
                resourceObj["environment_variables"] = envVarsArray;
            }

            dataArray.Add(resourceObj);
        }

        return dataArray.ToJsonString(s_jsonSerializerOptions);

        static JsonArray GetResourceRelationshipsJson(List<ResourceViewModel> allResources, ResourceViewModel resourceViewModel, Func<ResourceViewModel, string>? getResourceName)
        {
            var relationships = new JsonArray();

            foreach (var relationship in resourceViewModel.Relationships)
            {
                var matches = allResources
                    .Where(r => string.Equals(r.DisplayName, relationship.ResourceName, StringComparisons.ResourceName))
                    .Where(r => r.KnownState != KnownResourceState.Hidden)
                    .ToList();

                foreach (var match in matches)
                {
                    relationships.Add(new JsonObject
                    {
                        ["resource_name"] = getResourceName?.Invoke(match) ?? match.Name,
                        ["Types"] = relationship.Type
                    });
                }
            }

            return relationships;
        }

        static string? GetResourceHealthStatus(ResourceViewModel resource)
        {
            if (resource.HealthReports.Length == 0)
            {
                return "No health reports specified";
            }

            if (resource.HealthStatus == null && !resource.IsRunningState())
            {
                return $"Health reports aren't evaluated until the resource is in a {KnownResourceState.Running} state";
            }

            return resource.HealthStatus?.ToString();
        }

        static string? GetReportHealthStatus(ResourceViewModel resource, HealthReportViewModel report)
        {
            if (report.HealthStatus == null && !resource.IsRunningState())
            {
                return $"Health reports aren't evaluated until the resource is in a {KnownResourceState.Running} state";
            }

            return report.HealthStatus?.ToString();
        }
    }

    public static string? GetDashboardUrl(DashboardOptions options)
    {
        var frontendEndpoints = options.Frontend.GetEndpointAddresses();

        var frontendUrl = options.Frontend.PublicUrl
            ?? frontendEndpoints.FirstOrDefault(e => string.Equals(e.Scheme, "https", StringComparison.Ordinal))?.ToString()
            ?? frontendEndpoints.FirstOrDefault(e => string.Equals(e.Scheme, "http", StringComparison.Ordinal))?.ToString();

        return frontendUrl;
    }

    public static (string json, string limitMessage) GetStructuredLogsJson(OtlpTelemetryDataJson otlpData, DashboardOptions options, Func<IOtlpResource, string> getResourceName, bool includeDashboardUrl = false)
    {
        return SharedAIHelpers.GetStructuredLogsJson(otlpData.ResourceLogs, getResourceName, includeDashboardUrl ? GetDashboardUrl(options) : null);
    }

    internal static string GetStructuredLogJson(OtlpTelemetryDataJson otlpData, DashboardOptions options, Func<IOtlpResource, string> getResourceName, bool includeDashboardUrl = false)
    {
        return SharedAIHelpers.GetStructuredLogJson(otlpData.ResourceLogs, getResourceName, includeDashboardUrl ? GetDashboardUrl(options) : null);
    }

    public static bool TryGetSingleResult<T>(IEnumerable<T> source, Func<T, bool> predicate, [NotNullWhen(true)] out T? result)
    {
        result = default;
        var found = false;

        foreach (var item in source)
        {
            if (predicate(item))
            {
                if (found)
                {
                    // Multiple results found
                    result = default;
                    return false;
                }

                result = item;
                found = true;
            }
        }

        return found;
    }

    public static bool TryGetResource(IReadOnlyList<OtlpResource> resources, string resourceName, [NotNullWhen(true)] out OtlpResource? resource)
    {
        if (TryGetSingleResult(resources, r => r.ResourceName == resourceName, out resource))
        {
            return true;
        }
        else if (TryGetSingleResult(resources, r => r.ResourceKey.ToString() == resourceName, out resource))
        {
            return true;
        }

        resource = null;
        return false;
    }

    public static bool TryGetResource(IReadOnlyList<ResourceViewModel> resources, string resourceName, [NotNullWhen(true)] out ResourceViewModel? resource)
    {
        if (TryGetSingleResult(resources, r => r.Name == resourceName, out resource))
        {
            return true;
        }
        else if (TryGetSingleResult(resources, r => r.DisplayName == resourceName, out resource))
        {
            return true;
        }

        resource = null;
        return false;
    }

    /// <summary>
    /// Tries to resolve a resource name for telemetry queries.
    /// Returns true if no resource was specified or if the resource was found.
    /// Requires exact match - use the CLI to resolve base names to specific instances.
    /// </summary>
    public static bool TryResolveResourceForTelemetry(
        IReadOnlyList<OtlpResource> resources,
        string? resourceName,
        [NotNullWhen(false)] out string? errorMessage,
        out ResourceKey? resourceKey)
    {
        if (IsMissingValue(resourceName))
        {
            errorMessage = null;
            resourceKey = null;
            return true;
        }

        // Exact match only - the resource name must match either the full composite name
        // (e.g., "myapp-abc123") or a resource without an instance ID (e.g., "myapp")
        if (TryGetResource(resources, resourceName, out var resource))
        {
            errorMessage = null;
            resourceKey = resource.ResourceKey;
            return true;
        }

        errorMessage = $"Resource '{resourceName}' doesn't have any telemetry. The resource may not exist, may have failed to start or the resource might not support sending telemetry.";
        resourceKey = null;
        return false;
    }

    internal static async Task ExecuteStreamingCallAsync(
        IChatClient client,
        List<ChatMessage> chatMessages,
        Func<string, Task> textUpdateCallback,
        Func<IList<ChatMessage>, Task> onMessageCallback,
        int maximumResponseLength,
        AIFunction[] tools,
        CancellationTokenSource responseCts)
    {
        var chatOptions = new ChatOptions
        {
            Tools = tools
        };

        // This CTS is used to cancel the response stream if it takes too long to respond.
        // The timeout is reset each time a response update is received.
        var messageCts = new CancellationTokenSource();
        messageCts.Token.Register(responseCts.Cancel);
        if (!Debugger.IsAttached)
        {
            messageCts.CancelAfter(ResponseMessageTimeout);
        }

        var response = client.GetStreamingResponseAsync(chatMessages, chatOptions, responseCts.Token);

        var responseLength = 0;
        await foreach (var update in response.WithCancellation(responseCts.Token).ConfigureAwait(false))
        {
            if (!Debugger.IsAttached)
            {
                // Reset the timeout for the next update.
                messageCts.CancelAfter(ResponseMessageTimeout);
            }

            var newMessages = GetMessages(update, filter: c => c is not TextContent);
            if (newMessages.Count > 0)
            {
                await onMessageCallback(newMessages).ConfigureAwait(false);
            }

            foreach (var item in update.Contents.OfType<TextContent>())
            {
                if (!string.IsNullOrEmpty(item.Text))
                {
                    responseLength += item.Text.Length;

                    if (responseLength > maximumResponseLength)
                    {
                        throw new InvalidOperationException("Response exceeds maximum length.");
                    }

                    await textUpdateCallback(item.Text).ConfigureAwait(false);
                }
            }
        }
    }

    public static IList<ChatMessage> GetMessages(ChatResponseUpdate update, Func<AIContent, bool>? filter = null)
    {
        var contentsList = filter is null ? update.Contents : update.Contents.Where(filter).ToList();
        if (contentsList.Count > 0)
        {
            var list = new List<ChatMessage>();

            list.Add(new ChatMessage(update.Role ?? ChatRole.Assistant, contentsList)
            {
                AuthorName = update.AuthorName,
                RawRepresentation = update.RawRepresentation,
                AdditionalProperties = update.AdditionalProperties,
            });

            return list;
        }

        return [];
    }

    public static bool IsMissingValue([NotNullWhen(false)] string? value)
    {
        // Models sometimes pass an string value of "null" instead of null.
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsResourceAIOptOut(ResourceViewModel r)
    {
        return r.Properties.TryGetValue(KnownProperties.Resource.ExcludeFromMcp, out var v) && v.Value.TryConvertToBool(out var b) && b;
    }
}
