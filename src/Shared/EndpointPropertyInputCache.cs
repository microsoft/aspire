// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Reuses a publisher's endpoint-property inputs with stable, bounded names.
/// </summary>
internal sealed class EndpointPropertyInputCache
{
    private readonly Dictionary<string, ParameterResource> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedNames = new(StringComparer.OrdinalIgnoreCase);

    public void ReserveModelNames(IEnumerable<IResource> resources)
    {
        foreach (var resource in resources)
        {
            _reservedNames.Add(NormalizeName(resource.Name));
        }
    }

    public ParameterResource GetOrAdd(
        EndpointReferenceExpression reference,
        IConfiguration configuration,
        Func<string?>? defaultValueFactory = null)
    {
        if (!_inputs.TryGetValue(reference.ValueExpression, out var parameter))
        {
            var baseName = ResourceNameHelpers.GetEndpointPropertyInputName(
                reference.Endpoint.Resource.Name, reference.Endpoint.EndpointAnnotation.Name, reference.Property.ToString(), 64);
            var name = baseName;
            var suffix = 0;
            // Compose uppercases names and Helm/Bicep replace '-' with '_'. Reserve that
            // common projection too, so distinct parameters never share a published slot.
            while (!_reservedNames.Add(NormalizeName(name)))
            {
                var discriminator = $"-{(++suffix).ToString(CultureInfo.InvariantCulture)}";
                name = $"{baseName[..Math.Min(baseName.Length, 64 - discriminator.Length)]}{discriminator}";
            }
            parameter = ParameterResourceBuilderExtensions.CreateParameter(configuration, name, secret: false, defaultValueFactory?.Invoke());
            _inputs.Add(reference.ValueExpression, parameter);
        }

        return parameter;
    }

    private static string NormalizeName(string name) => name.Replace('-', '_');
}
