// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREAZURE001

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure;

internal sealed class AzureSandboxEndpointPropertyValueProvider : IValueProvider, IManifestExpressionProvider, IValueWithReferences
{
    private readonly AzureSandboxContainerResource _resource;
    private readonly EndpointReferenceExpression _endpointReferenceExpression;
    private readonly AzureSandboxContainerDeployment.SandboxEndpoint _sandboxEndpoint;

    public AzureSandboxEndpointPropertyValueProvider(
        AzureSandboxContainerResource resource,
        EndpointReferenceExpression endpointReferenceExpression)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(endpointReferenceExpression);

        var endpoint = endpointReferenceExpression.Endpoint.EndpointAnnotation;
        if (!endpoint.IsExternal)
        {
            throw new InvalidOperationException($"Endpoint '{endpoint.Name}' on resource '{resource.TargetResource.Name}' is not external. Azure sandbox endpoint references are only supported for external HTTP endpoints because ADC only assigns public URLs to exposed ports.");
        }

        _resource = resource;
        _endpointReferenceExpression = endpointReferenceExpression;
        _sandboxEndpoint = ResolveSandboxEndpoint(resource, endpointReferenceExpression.Endpoint);
    }

    public string ValueExpression =>
        $"{{{_resource.Name}.endpoints.{_endpointReferenceExpression.Endpoint.EndpointName}.{_endpointReferenceExpression.Property.ToString().ToLowerInvariant()}}}";

    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        return new(GetKnownValueWithoutDeploymentState() ?? string.Empty);
    }

    public async ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default)
    {
        if (GetKnownValueWithoutDeploymentState() is { } knownValue)
        {
            return knownValue;
        }

        if (context.ExecutionContext is null)
        {
            return string.Empty;
        }

        var deploymentStateManager = context.ExecutionContext.Services.GetRequiredService<IDeploymentStateManager>();
        var stateSection = await deploymentStateManager
            .AcquireSectionAsync(AzureSandboxContainerDeployment.GetStateSectionName(_resource), cancellationToken)
            .ConfigureAwait(false);

        if (TryGetUrl(stateSection, out var url))
        {
            return GetValueFromUrl(url);
        }

        // The first deployment cannot know the ADC proxy URL yet because it is assigned after
        // the sandbox deploy step exposes the port. Return an empty value instead of failing
        // Azure provisioning so a subsequent deploy can converge from the persisted URL.
        return string.Empty;
    }

    IEnumerable<object> IValueWithReferences.References =>
    [
        _resource,
        _endpointReferenceExpression.Endpoint.Resource
    ];

    private string? GetKnownValueWithoutDeploymentState()
    {
        return _endpointReferenceExpression.Property switch
        {
            EndpointProperty.TargetPort => _sandboxEndpoint.TargetPort.ToString(CultureInfo.InvariantCulture),
            EndpointProperty.Scheme => Uri.UriSchemeHttps,
            EndpointProperty.Port => "443",
            EndpointProperty.TlsEnabled => bool.TrueString,
            _ => null
        };
    }

    private string GetValueFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"Azure sandbox deployment state for resource '{_resource.TargetResource.Name}' endpoint '{_endpointReferenceExpression.Endpoint.EndpointName}' contains invalid URL '{url}'.");
        }

        return _endpointReferenceExpression.Property switch
        {
            EndpointProperty.Url => url,
            EndpointProperty.Host or EndpointProperty.IPV4Host => uri.Host,
            EndpointProperty.Port => uri.Port.ToString(CultureInfo.InvariantCulture),
            EndpointProperty.TargetPort => _sandboxEndpoint.TargetPort.ToString(CultureInfo.InvariantCulture),
            EndpointProperty.Scheme => uri.Scheme,
            EndpointProperty.HostAndPort => uri.IsDefaultPort ? uri.Host : uri.Authority,
            EndpointProperty.TlsEnabled => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? bool.TrueString : bool.FalseString,
            _ => throw new InvalidOperationException($"The property '{_endpointReferenceExpression.Property}' is not supported for the endpoint '{_endpointReferenceExpression.Endpoint.EndpointName}'.")
        };
    }

    private bool TryGetUrl(DeploymentStateSection stateSection, [NotNullWhen(true)] out string? url)
    {
        url = null;

        // Sandbox deployment state stores exposed ADC ports as:
        //   { "Ports": [{ "Name": "http", "Port": 8080, "Url": "https://<sandbox-id>--8080.<region>.adcproxy.io/" }] }
        // Multiple Aspire endpoints can share the same target port, so fall back to target-port
        // matching when the persisted representative name differs from the requested endpoint name.
        if (stateSection.Data["Ports"] is not JsonArray ports)
        {
            return false;
        }

        JsonObject? fallbackPort = null;
        foreach (var port in ports.OfType<JsonObject>())
        {
            if (port["Port"]?.GetValue<int>() == _sandboxEndpoint.TargetPort)
            {
                fallbackPort ??= port;
            }

            if (string.Equals(port["Name"]?.GetValue<string>(), _sandboxEndpoint.Name, StringComparison.Ordinal))
            {
                return TryGetUrl(port, out url);
            }
        }

        return fallbackPort is not null && TryGetUrl(fallbackPort, out url);
    }

    private static bool TryGetUrl(JsonObject port, [NotNullWhen(true)] out string? url)
    {
        url = port["Url"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(url);
    }

    private static AzureSandboxContainerDeployment.SandboxEndpoint ResolveSandboxEndpoint(AzureSandboxContainerResource resource, EndpointReference endpointReference)
    {
        var endpoints = AzureSandboxContainerDeployment.ResolveSandboxEndpoints(resource);
        var endpointName = endpointReference.EndpointName;
        var resolvedTargetPort = ResolveEndpointTargetPort(resource, endpointName) ?? endpointReference.EndpointAnnotation.TargetPort;
        AzureSandboxContainerDeployment.SandboxEndpoint? fallbackEndpoint = null;

        foreach (var endpoint in endpoints)
        {
            if (resolvedTargetPort is int targetPort &&
                endpoint.TargetPort == targetPort)
            {
                fallbackEndpoint ??= endpoint;
            }

            if (string.Equals(endpoint.Name, endpointName, StringComparison.Ordinal))
            {
                return endpoint;
            }
        }

        if (fallbackEndpoint is { } matchedEndpoint)
        {
            return matchedEndpoint;
        }

        throw new InvalidOperationException($"Endpoint '{endpointName}' on resource '{resource.TargetResource.Name}' is not exposed by the Azure sandbox deployment target. Configure it as an external HTTP endpoint before referencing its sandbox URL.");
    }

    private static int? ResolveEndpointTargetPort(AzureSandboxContainerResource resource, string endpointName)
    {
        foreach (var resolvedEndpoint in resource.TargetResource.ResolveEndpoints())
        {
            if (string.Equals(resolvedEndpoint.Endpoint.Name, endpointName, StringComparison.Ordinal) &&
                resolvedEndpoint.TargetPort.Value is int targetPort)
            {
                return targetPort;
            }
        }

        return null;
    }
}
