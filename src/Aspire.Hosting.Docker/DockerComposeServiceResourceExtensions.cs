// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;

internal static class DockerComposeServiceResourceExtensions
{
    internal static async Task<object> ProcessValueAsync(this DockerComposeServiceResource resource, object value, IConfiguration configuration)
    {
        while (true)
        {
            if (value is string s)
            {
                return s;
            }

            if (value is EndpointReference ep)
            {
                var referencedResource = resource.Parent.ResourceMapping[ep.Resource];

                var mapping = referencedResource.EndpointMappings[ep.EndpointName];

                var url = GetValue(mapping, EndpointProperty.Url);

                return url;
            }

            if (value is ParameterResource param)
            {
                return param.AsEnvironmentPlaceholder(resource);
            }

            if (value is ConnectionStringReference cs)
            {
                value = cs.ConnectionStringExpression;
                continue;
            }

            if (value is IResourceWithConnectionString csrs)
            {
                value = csrs.ConnectionStringExpression;
                continue;
            }

            if (value is EndpointReferenceExpression epExpr)
            {
                var referencedResource = resource.Parent.ResourceMapping[epExpr.Endpoint.Resource];

                var mapping = referencedResource.EndpointMappings[epExpr.Endpoint.EndpointName];

                return epExpr.Property switch
                {
                    EndpointProperty.BrowserOrigins or EndpointProperty.BrowserOrigin or EndpointProperty.BrowserOriginsJson =>
                        GetEndpointInput(epExpr, mapping).AsEnvironmentPlaceholder(resource),
                    _ => GetValue(mapping, epExpr.Property)
                };
            }

            if (value is ReferenceExpression expr)
            {
                // Handle conditional expressions by resolving the condition to its
                // actual value. Docker Compose YAML cannot represent conditionals, so
                // the branch must be selected at generation time.
                if (expr.IsConditional)
                {
                    var conditionStr = await expr.Condition!.GetValueAsync(default).ConfigureAwait(false);

                    var branch = string.Equals(conditionStr, expr.MatchValue, StringComparison.OrdinalIgnoreCase)
                        ? expr.WhenTrue!
                        : expr.WhenFalse!;
                    return await resource.ProcessValueAsync(branch, configuration).ConfigureAwait(false);
                }

                if (expr is { Format: "{0}", ValueProviders.Count: 1 })
                {
                    return (await resource.ProcessValueAsync(expr.ValueProviders[0], configuration).ConfigureAwait(false)).ToString() ?? string.Empty;
                }

                var args = new object[expr.ValueProviders.Count];
                var index = 0;

                foreach (var vp in expr.ValueProviders)
                {
                    var val = await resource.ProcessValueAsync(vp, configuration).ConfigureAwait(false);
                    args[index++] = val ?? throw new InvalidOperationException("Value is null");
                }

                return string.Format(CultureInfo.InvariantCulture, expr.Format, args);
            }

            // If we don't know how to process the value, we just return it as an external reference
            if (value is IManifestExpressionProvider r)
            {
                return r.AsEnvironmentPlaceholder(resource);
            }

            return value; // todo: we need to never get here really...
        }

        ParameterResource GetEndpointInput(EndpointReferenceExpression reference, DockerComposeServiceResource.EndpointMapping mapping)
        {
            if (resource.Parent.EndpointInputs.TryGetValue(reference.ValueExpression, out var parameter))
            {
                return parameter;
            }

            var name = ResourceNameHelpers.GetEndpointPropertyInputName(
                reference.Endpoint.Resource.Name, reference.Endpoint.EndpointAnnotation.Name, reference.Property.ToString(), 64);
            string? defaultValue = null;
            if (mapping.IsExternal && mapping.ExposedPort is int port && mapping.Scheme is "http" or "https")
            {
                var origin = new UriBuilder(mapping.Scheme, "localhost", port).Uri
                    .GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
                defaultValue = reference.Property == EndpointProperty.BrowserOriginsJson
                    ? System.Text.Json.JsonSerializer.Serialize(new[] { origin })
                    : origin;
            }

            parameter = ParameterResourceBuilderExtensions.CreateParameter(configuration, name, secret: false, defaultValue);
            resource.Parent.EndpointInputs.Add(reference.ValueExpression, parameter);
            return parameter;
        }
    }

    private static string GetValue(DockerComposeServiceResource.EndpointMapping mapping, EndpointProperty property)
    {
        return property switch
        {
            EndpointProperty.Url => GetHostValue($"{mapping.Scheme}://", $":{mapping.InternalPort}"),
            EndpointProperty.Host or EndpointProperty.IPV4Host => GetHostValue(),
            EndpointProperty.Port => mapping.InternalPort,
            EndpointProperty.HostAndPort => GetHostValue(suffix: $":{mapping.InternalPort}"),
            EndpointProperty.TargetPort => $"{mapping.InternalPort}",
            EndpointProperty.Scheme => mapping.Scheme,
            _ => throw new NotSupportedException(),
        };

        string GetHostValue(string? prefix = null, string? suffix = null)
        {
            return $"{prefix}{mapping.Host}{suffix}";
        }
    }
}
