// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring OpenTelemetry in projects using environment variables.
/// </summary>
public static class OtlpConfigurationExtensions
{
    /// <summary>
    /// The name of the environment variable for configuring the OTLP exporter ingestion URL. This is used by OpenTelemetry SDKs to determine where to send telemetry data.
    /// </summary>
    public static readonly string OtlpEndpointEnvironmentVariableName = KnownOtelConfigNames.ExporterOtlpEndpoint;

    /// <summary>
    /// Configures OpenTelemetry in projects using environment variables.
    /// </summary>
    /// <param name="resource">The resource to add annotations to.</param>
    /// <param name="configuration">The configuration to use for the OTLP exporter endpoint URL.</param>
    /// <param name="environment">The host environment to check if the application is running in development mode.</param>
    public static void AddOtlpEnvironment(IResource resource, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Add annotation to mark this resource as having OTLP exporter configured
        resource.Annotations.Add(new OtlpExporterAnnotation());

        RegisterOtlpEnvironment(resource, configuration, environment);
    }

    /// <summary>
    /// Configures OpenTelemetry in projects using environment variables.
    /// </summary>
    /// <param name="resource">The resource to add annotations to.</param>
    /// <param name="configuration">The configuration to use for the OTLP exporter endpoint URL.</param>
    /// <param name="environment">The host environment to check if the application is running in development mode.</param>
    /// <param name="protocol">The protocol to use for the OTLP exporter. If not set, it will try gRPC then Http.</param>
    public static void AddOtlpEnvironment(IResource resource, IConfiguration configuration, IHostEnvironment environment, OtlpProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Add annotation to mark this resource as having OTLP exporter configured with a required protocol
        resource.Annotations.Add(new OtlpExporterAnnotation { RequiredProtocol = protocol });

        RegisterOtlpEnvironment(resource, configuration, environment);
    }

    private static void RegisterOtlpEnvironment(IResource resource, IConfiguration configuration, IHostEnvironment environment)
    {
        // Configure OpenTelemetry in projects using environment variables.
        // https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/configuration/sdk-environment-variables.md

        resource.Annotations.Add(new EnvironmentCallbackAnnotation(async context =>
        {
            if (context.ExecutionContext.IsPublishMode)
            {
                // REVIEW:  Do we want to set references to an imaginary otlp provider as a requirement?
                return;
            }

            if (!resource.TryGetLastAnnotation<OtlpExporterAnnotation>(out var otlpExporterAnnotation))
            {
                return;
            }

            var (url, protocol) = OtlpEndpointResolver.ResolveOtlpEndpoint(configuration, otlpExporterAnnotation.RequiredProtocol);
            context.EnvironmentVariables[OtlpEndpointEnvironmentVariableName] = new HostUrl(url);
            context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol] = protocol;

            // Set the service name and instance id to the resource name and UID. Values are injected by DCP.
            context.EnvironmentVariables[KnownOtelConfigNames.ResourceAttributes] = "service.instance.id={{- index .Annotations \"" + CustomResource.OtelServiceInstanceIdAnnotation + "\" -}}";
            context.EnvironmentVariables[KnownOtelConfigNames.ServiceName] = "{{- index .Annotations \"" + CustomResource.OtelServiceNameAnnotation + "\" -}}";

            if (configuration["AppHost:OtlpApiKey"] is { } otlpApiKey)
            {
                context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpHeaders] = $"x-otlp-api-key={otlpApiKey}";
            }

            // Configure OTLP to quickly provide all data with a small delay in development.
            if (environment.IsDevelopment())
            {
                // Set a small batch schedule delay in development.
                // This reduces the delay that OTLP exporter waits to sends telemetry and makes the dashboard telemetry pages responsive.
                var value = "1000"; // milliseconds
                context.EnvironmentVariables[KnownOtelConfigNames.BlrpScheduleDelay] = value;
                context.EnvironmentVariables[KnownOtelConfigNames.BspScheduleDelay] = value;
                context.EnvironmentVariables[KnownOtelConfigNames.MetricExportInterval] = value;

                // Configure trace sampler to send all traces to the dashboard.
                context.EnvironmentVariables[KnownOtelConfigNames.TracesSampler] = "always_on";
                // Configure metrics to include exemplars.
                context.EnvironmentVariables[KnownOtelConfigNames.MetricsExemplarFilter] = "trace_based";

                // Output sensitive message content for GenAI.
                // A convention for libraries that output GenAI telemetry is to use `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` env var.
                // See:
                // - https://opentelemetry.io/blog/2024/otel-generative-ai/
                // - https://github.com/search?q=org%3Aopen-telemetry+OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT&type=code
                context.EnvironmentVariables[KnownOtelConfigNames.InstrumentationGenAiCaptureMessageContent] = "true";
            }
        }));
    }

    /// <summary>
    /// Injects the appropriate environment variables to allow the resource to enable sending telemetry to the dashboard.
    /// <list type="number">
    ///   <item>It sets the OTLP endpoint to the value of the <c>ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL</c> environment variable.</item>
    ///   <item>It sets the service name and instance id to the resource name and UID. Values are injected by the orchestrator.</item>
    ///   <item>It sets a small batch schedule delay in development. This reduces the delay that OTLP exporter waits to sends telemetry and makes the dashboard telemetry pages responsive.</item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withOtlpExporter", Description = "Configures OTLP telemetry export")]
    public static IResourceBuilder<T> WithOtlpExporter<T>(this IResourceBuilder<T> builder) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddOtlpEnvironment(builder.Resource, builder.ApplicationBuilder.Configuration, builder.ApplicationBuilder.Environment);

        return builder;
    }

    /// <summary>
    /// Injects the appropriate environment variables to allow the resource to enable sending telemetry to the dashboard.
    /// <list type="number">
    ///   <item>It sets the OTLP endpoint to the value of the <c>ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL</c> environment variable.</item>
    ///   <item>It sets the service name and instance id to the resource name and UID. Values are injected by the orchestrator.</item>
    ///   <item>It sets a small batch schedule delay in development. This reduces the delay that OTLP exporter waits to sends telemetry and makes the dashboard telemetry pages responsive.</item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="protocol">The protocol to use for the OTLP exporter. If not set, it will try gRPC then Http.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withOtlpExporterProtocol", Description = "Configures OTLP telemetry export with specific protocol")]
    public static IResourceBuilder<T> WithOtlpExporter<T>(this IResourceBuilder<T> builder, OtlpProtocol protocol) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddOtlpEnvironment(builder.Resource, builder.ApplicationBuilder.Configuration, builder.ApplicationBuilder.Environment, protocol);

        return builder;
    }
}
