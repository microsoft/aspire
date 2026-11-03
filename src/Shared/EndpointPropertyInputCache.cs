// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Reuses a publisher's endpoint-property inputs with stable, bounded names.
/// </summary>
internal sealed class EndpointPropertyInputCache
{
    private readonly Dictionary<string, ParameterResource> _inputs = new(StringComparer.OrdinalIgnoreCase);

    public ParameterResource GetOrAdd(
        EndpointReferenceExpression reference,
        IConfiguration configuration,
        Func<string?>? defaultValueFactory = null)
    {
        if (!_inputs.TryGetValue(reference.ValueExpression, out var parameter))
        {
            var name = ResourceNameHelpers.GetEndpointPropertyInputName(
                reference.Endpoint.Resource.Name, reference.Endpoint.EndpointAnnotation.Name, reference.Property.ToString(), 64);
            parameter = ParameterResourceBuilderExtensions.CreateParameter(configuration, name, secret: false, defaultValueFactory?.Invoke());
            _inputs.Add(reference.ValueExpression, parameter);
        }

        return parameter;
    }
}
