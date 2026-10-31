// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Gathers environment variables for resources.
/// </summary>
internal class EnvironmentVariablesExecutionConfigurationGatherer : IExecutionConfigurationGatherer
{
    /// <inheritdoc/>
    public async ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource, ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default)
    {
        if (resource.TryGetEnvironmentVariables(out var envVarAnnotations))
        {
            var envVars = new Dictionary<string, object>(context.EnvironmentVariables);
            var callbackContext = new EnvironmentCallbackContext(executionContext, resource, envVars, cancellationToken: cancellationToken)
            {
                Logger = resourceLogger,
            };

            // The OTLP exporter callback puts service.instance.id in OTEL_RESOURCE_ATTRIBUTES, and the dashboard needs it
            // to match telemetry to the resource and to tell replicas apart. AddProject registers that callback before any
            // callback the AppHost adds, so a later WithEnvironment("OTEL_RESOURCE_ATTRIBUTES", ...) replaces the whole
            // value. Remember the last id any callback produced and add it back after all callbacks ran. The last id seen
            // is used rather than the DCP template because a callback can resolve the template: MAUI platform resources
            // replace it with a GUID. Publish mode is left alone because the values go to the deployment target as written.
            var keepServiceInstanceId = executionContext.IsRunMode && resource.HasAnnotationOfType<OtlpExporterAnnotation>();
            string? serviceInstanceId = null;

            foreach (var ann in envVarAnnotations)
            {
                // Each annotation operates on a shared context.
                envVars = await ann.AsCallbackAnnotation().EvaluateOnceAsync(callbackContext).ConfigureAwait(false);

                if (keepServiceInstanceId)
                {
                    serviceInstanceId = OtlpConfigurationExtensions.GetServiceInstanceId(envVars) ?? serviceInstanceId;
                }
            }

            if (serviceInstanceId is not null)
            {
                envVars = OtlpConfigurationExtensions.RestoreServiceInstanceId(envVars, serviceInstanceId);
            }

            // Take the final result and apply to the gatherer context.
            foreach (var kvp in envVars)
            {
                context.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }
    }
}