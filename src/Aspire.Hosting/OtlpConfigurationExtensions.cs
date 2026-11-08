// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring OpenTelemetry in projects using environment variables.
/// </summary>
public static class OtlpConfigurationExtensions
{
    private const string ServiceInstanceIdAttributeName = "service.instance.id";

    // DCP replaces this template with the instance id of each replica when it starts the process or container.
    private const string ServiceInstanceIdTemplate = "{{- index .Annotations \"" + CustomResource.OtelServiceInstanceIdAnnotation + "\" -}}";

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

    /// <summary>
    /// Configures OTLP export only when the required collector endpoint is available.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="protocol">The required OTLP protocol.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The exporter annotation is retained when no endpoint is available, but OTLP environment variables are
    /// omitted so workloads that can operate without telemetry remain runnable.
    /// </remarks>
    [AspireExportIgnore(Reason = "Optional OTLP exporter registration is currently used by C# hosting integrations only.")]
    public static IResourceBuilder<T> WithOtlpExporterIfEndpointAvailable<T>(
        this IResourceBuilder<T> builder,
        OtlpProtocol protocol) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.Annotations.Add(new OtlpExporterAnnotation { RequiredProtocol = protocol });
        RegisterOtlpEnvironment(
            builder.Resource,
            builder.ApplicationBuilder.Configuration,
            builder.ApplicationBuilder.Environment,
            skipIfEndpointUnavailable: true);

        return builder;
    }

    private static void RegisterOtlpEnvironment(
        IResource resource,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool skipIfEndpointUnavailable = false)
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

            var otlpExporterAnnotation = GetEffectiveOtlpExporterAnnotation(resource);
            if (otlpExporterAnnotation is null)
            {
                return;
            }

            var dashboardEndpoint = ResolveOtlpEndpointFromDashboard(context, otlpExporterAnnotation.RequiredProtocol);

            if (dashboardEndpoint is not null)
            {
                // Use the dashboard endpoint reference directly. This resolves to the actual allocated URL,
                // including when ports are randomized (e.g. isolated mode).
                context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpEndpoint] = dashboardEndpoint.Value.Endpoint;
                context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol] = dashboardEndpoint.Value.Protocol;
            }
            else
            {
                if (skipIfEndpointUnavailable &&
                    !HasConfiguredOtlpEndpoint(configuration, otlpExporterAnnotation.RequiredProtocol))
                {
                    return;
                }

                // Fall back to resolving from configuration. This is the case when the dashboard resource
                // is not in the model (e.g. in tests or publish mode).
                var (url, protocol) = OtlpEndpointResolver.ResolveOtlpEndpoint(configuration, otlpExporterAnnotation.RequiredProtocol);
                context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpEndpoint] = new HostUrl(url);
                context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol] = protocol;
            }

            // Runtime-specific exporters are activated in the same step that confirms an endpoint exists.
            // This keeps activation independent of environment callback ordering and avoids enabling exporters
            // for dashboard-free workloads.
            ApplyActivationEnvironmentVariables(resource, context.EnvironmentVariables);

            // Set the service name and instance id to the resource name and UID. Values are injected by DCP.
            // OTEL_RESOURCE_ATTRIBUTES can already hold attributes from a callback that ran earlier, for example
            // WithEnvironment("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment=dev") before WithOtlpExporter() on a
            // container, so add the instance id to that value instead of replacing it. An explicit service.instance.id
            // is kept. A value that is not a plain string (for example a ReferenceExpression) cannot be inspected
            // before it resolves, so it is replaced as before.
            context.EnvironmentVariables.TryGetValue(KnownOtelConfigNames.ResourceAttributes, out var resourceAttributes);
            context.EnvironmentVariables[KnownOtelConfigNames.ResourceAttributes] = AddResourceAttributeIfMissing(
                resourceAttributes as string,
                ServiceInstanceIdAttributeName,
                ServiceInstanceIdTemplate);
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

    private static bool HasConfiguredOtlpEndpoint(IConfiguration configuration, OtlpProtocol? requiredProtocol)
    {
        var grpcEndpoint = configuration.GetString(
            KnownConfigNames.DashboardOtlpGrpcEndpointUrl,
            KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl);
        var httpEndpoint = configuration.GetString(
            KnownConfigNames.DashboardOtlpHttpEndpointUrl,
            KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl);

        return requiredProtocol switch
        {
            OtlpProtocol.Grpc => !string.IsNullOrWhiteSpace(grpcEndpoint),
            OtlpProtocol.HttpProtobuf or OtlpProtocol.HttpJson => !string.IsNullOrWhiteSpace(httpEndpoint),
            _ => !string.IsNullOrWhiteSpace(grpcEndpoint) || !string.IsNullOrWhiteSpace(httpEndpoint),
        };
    }

    private static void ApplyActivationEnvironmentVariables(
        IResource resource,
        IDictionary<string, object> environmentVariables)
    {
        if (GetEffectiveOtlpExporterAnnotation(resource) is not IReadOnlyDictionary<string, string> activationEnvironmentVariables)
        {
            return;
        }

        foreach (var (name, value) in activationEnvironmentVariables)
        {
            environmentVariables.TryAdd(name, value);
        }
    }

    private static OtlpExporterAnnotation? GetEffectiveOtlpExporterAnnotation(IResource resource)
        => resource.Annotations.OfType<OtlpExporterAnnotation>().LastOrDefault();

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
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOtlpExporter dispatcher export.")]
    public static IResourceBuilder<T> WithOtlpExporter<T>(this IResourceBuilder<T> builder) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddOtlpEnvironment(builder.Resource, builder.ApplicationBuilder.Configuration, builder.ApplicationBuilder.Environment);

        return builder;
    }

    /// <summary>
    /// Configures OTLP telemetry export
    /// </summary>
    [AspireExport("withOtlpExporter")]
    internal static IResourceBuilder<T> WithOtlpExporterForPolyglot<T>(
        this IResourceBuilder<T> builder,
        OtlpProtocol? protocol = null) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        return protocol is null
            ? builder.WithOtlpExporter()
            : builder.WithOtlpExporter(protocol.Value);
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
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOtlpExporter dispatcher export.")]
    public static IResourceBuilder<T> WithOtlpExporter<T>(this IResourceBuilder<T> builder, OtlpProtocol protocol) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddOtlpEnvironment(builder.Resource, builder.ApplicationBuilder.Configuration, builder.ApplicationBuilder.Environment, protocol);

        return builder;
    }

    /// <summary>
    /// Tries to resolve the OTLP endpoint from the dashboard resource in the distributed application model.
    /// This ensures that when ports are randomized (e.g. isolated mode), resources use the actual
    /// allocated endpoint rather than the statically configured port.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="EndpointReference"/> has no network context baked in, so it resolves
    /// using the calling resource's network at evaluation time. This means containers automatically
    /// get container-network URLs and non-containers get localhost URLs.
    /// </remarks>
    private static (EndpointReference Endpoint, string Protocol)? ResolveOtlpEndpointFromDashboard(EnvironmentCallbackContext context, OtlpProtocol? requiredProtocol)
    {
        DistributedApplicationModel? model;
        try
        {
            model = context.ExecutionContext.Services.GetService<DistributedApplicationModel>();
        }
        catch (InvalidOperationException)
        {
            // ServiceProvider may not be available if the container hasn't been built yet
            // (e.g. env var evaluation during testing without a fully built host).
            return null;
        }

        if (model is null)
        {
            return null;
        }

        if (!model.Resources.TryGetByName(KnownResourceNames.AspireDashboard, out var resource) || resource is not IResourceWithEndpoints dashboardResource)
        {
            return null;
        }

        var grpcEndpoint = dashboardResource.GetEndpoint(KnownEndpointNames.OtlpGrpcEndpointName);
        var httpEndpoint = dashboardResource.GetEndpoint(KnownEndpointNames.OtlpHttpEndpointName);

        return (requiredProtocol, grpcEndpoint.Exists, httpEndpoint.Exists) switch
        {
            (OtlpProtocol.Grpc, true, _) => (grpcEndpoint, "grpc"),
            (OtlpProtocol.HttpProtobuf, _, true) => (httpEndpoint, "http/protobuf"),
            (OtlpProtocol.HttpJson, _, true) => (httpEndpoint, "http/json"),
            (_, true, _) => (grpcEndpoint, "grpc"),
            (_, _, true) => (httpEndpoint, "http/protobuf"),
            _ => null
        };
    }

    /// <summary>
    /// Gets the <c>service.instance.id</c> value in the <c>OTEL_RESOURCE_ATTRIBUTES</c> environment variable.
    /// </summary>
    /// <param name="environmentVariables">The environment variables of a resource.</param>
    /// <returns>The value, or <see langword="null"/> when the variable is missing, is not a string, or has no such attribute.</returns>
    internal static string? GetServiceInstanceId(Dictionary<string, object> environmentVariables)
    {
        return environmentVariables.TryGetValue(KnownOtelConfigNames.ResourceAttributes, out var value) &&
            value is string resourceAttributes &&
            TryGetResourceAttribute(resourceAttributes, ServiceInstanceIdAttributeName, out var serviceInstanceId)
            ? serviceInstanceId
            : null;
    }

    /// <summary>
    /// Adds <c>service.instance.id</c> back to the <c>OTEL_RESOURCE_ATTRIBUTES</c> environment variable when its value no longer has one.
    /// </summary>
    /// <param name="environmentVariables">The environment variables of a resource.</param>
    /// <param name="serviceInstanceId">The <c>service.instance.id</c> value to add.</param>
    /// <returns>
    /// <paramref name="environmentVariables"/> when nothing changes; otherwise a copy with the attribute added. The input is
    /// not modified because it can be the cached result of an environment callback.
    /// </returns>
    internal static Dictionary<string, object> RestoreServiceInstanceId(Dictionary<string, object> environmentVariables, string serviceInstanceId)
    {
        // A removed variable stays removed, since removing it is a deliberate choice. A value that is not a plain string
        // (for example a ReferenceExpression) is left alone because it cannot be inspected before it resolves.
        if (!environmentVariables.TryGetValue(KnownOtelConfigNames.ResourceAttributes, out var value) ||
            value is not string resourceAttributes ||
            TryGetResourceAttribute(resourceAttributes, ServiceInstanceIdAttributeName, out _))
        {
            return environmentVariables;
        }

        return new Dictionary<string, object>(environmentVariables)
        {
            [KnownOtelConfigNames.ResourceAttributes] = AddResourceAttributeIfMissing(resourceAttributes, ServiceInstanceIdAttributeName, serviceInstanceId)
        };
    }

    /// <summary>
    /// Adds an attribute to an <c>OTEL_RESOURCE_ATTRIBUTES</c> value unless the value already has an attribute with that key.
    /// </summary>
    internal static string AddResourceAttributeIfMissing(string? resourceAttributes, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(resourceAttributes))
        {
            return $"{key}={value}";
        }

        return TryGetResourceAttribute(resourceAttributes, key, out _)
            ? resourceAttributes
            : $"{resourceAttributes},{key}={value}";
    }

    /// <summary>
    /// Gets the value of an attribute in an <c>OTEL_RESOURCE_ATTRIBUTES</c> value.
    /// </summary>
    internal static bool TryGetResourceAttribute(string resourceAttributes, string key, [NotNullWhen(true)] out string? value)
    {
        // OTEL_RESOURCE_ATTRIBUTES is a comma-separated list of key=value pairs, for example:
        //   deployment.environment=dev,service.instance.id=api-x7k2
        // ',' and '=' inside keys and values must be percent-encoded, so splitting on every ',' and then on the first
        // '=' finds each pair. Whitespace around a key is ignored and an entry without '=' is skipped, as the
        // OpenTelemetry .NET SDK does. The value is returned as written, still percent-encoded.
        // https://opentelemetry.io/docs/specs/otel/configuration/sdk-environment-variables/
        // https://opentelemetry.io/docs/specs/otel/resource/sdk/#specifying-resource-information-via-an-environment-variable
        foreach (var pair in resourceAttributes.Split(','))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex >= 0 && pair.AsSpan(0, separatorIndex).Trim().Equals(key, StringComparison.Ordinal))
            {
                value = pair[(separatorIndex + 1)..];
                return true;
            }
        }

        value = null;
        return false;
    }
}
