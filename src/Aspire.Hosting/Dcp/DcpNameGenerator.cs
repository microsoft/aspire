// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

internal sealed class DcpNameGenerator
{
    // A random suffix added to every DCP object name ensures that those names (and derived object names, for example container names)
    // are unique machine-wide with a high level of probability.
    // The length of 8 achieves that while keeping the names relatively short and readable.
    // The second purpose of the suffix is to play a role of a unique OpenTelemetry service instance ID.
    private const int RandomNameSuffixLength = 8;
    private readonly IConfiguration _configuration;
    private readonly IOptions<DcpOptions> _options;

    public DcpNameGenerator(IConfiguration configuration, IOptions<DcpOptions> options)
    {
        _configuration = configuration;
        _options = options;
    }

    public void EnsureDcpInstancesPopulated(IResource resource)
    {
        if (resource.TryGetInstances(out _))
        {
            return;
        }

        if (resource.IsContainer())
        {
            var (name, suffix) = GetContainerName(resource);
            AddInstancesAnnotation(resource, [new DcpInstance(name, suffix, 0)]);
        }
        else if (resource is ExecutableResource or ContainerExecutableResource)
        {
            var (name, suffix) = GetExecutableName(resource);
            AddInstancesAnnotation(resource, [new DcpInstance(name, suffix, 0)]);
        }
        else if (resource is ProjectResource)
        {
            var replicas = resource.GetReplicaCount();
            var builder = ImmutableArray.CreateBuilder<DcpInstance>(replicas);
            for (var i = 0; i < replicas; i++)
            {
                var (name, suffix) = GetExecutableName(resource);
                builder.Add(new DcpInstance(name, suffix, i));
            }
            AddInstancesAnnotation(resource, builder.ToImmutable());
        }
    }

    private static void AddInstancesAnnotation(IResource resource, ImmutableArray<DcpInstance> instances)
    {
        resource.Annotations.Add(new DcpInstancesAnnotation(instances));
    }

    public (string Name, string Suffix) GetContainerName(IResource container)
    {
        var nameSuffix = container.GetContainerLifetimeType() switch
        {
            ContainerLifetime.Session => GetRandomNameSuffix(),
            _ => GetProjectHashSuffix(),
        };

        return (GetObjectNameForResource(container, _options.Value, nameSuffix), nameSuffix);
    }

    public (string Name, string Suffix) GetExecutableName(IResource project)
    {
        var nameSuffix = GetRandomNameSuffix();
        return (GetObjectNameForResource(project, _options.Value, nameSuffix), nameSuffix);
    }

    public string GetServiceName(IResource resource, EndpointAnnotation endpoint, bool hasMultipleEndpoints, HashSet<string> allServiceNames)
    {
        var candidateServiceName = !hasMultipleEndpoints
            ? GetObjectNameForResource(resource, _options.Value)
            : GetObjectNameForResource(resource, _options.Value, endpoint.Name);

        return GenerateUniqueServiceName(allServiceNames, candidateServiceName);
    }

    private static string GenerateUniqueServiceName(HashSet<string> serviceNames, string candidateName)
    {
        int suffix = 1;
        string uniqueName = candidateName;

        while (!serviceNames.Add(uniqueName))
        {
            uniqueName = $"{candidateName}-{suffix}";
            suffix++;
            if (suffix == 100)
            {
                // Should never happen, but we do not want to ever get into a infinite loop situation either.
                throw new ArgumentException($"Could not generate a unique name for service '{candidateName}'");
            }
        }

        return uniqueName;
    }

    public static string GetRandomNameSuffix()
    {
        // RandomNameSuffixLength of lowercase characters
        var suffix = PasswordGenerator.Generate(RandomNameSuffixLength, true, false, false, false, RandomNameSuffixLength, 0, 0, 0);
        return suffix;
    }

    public string GetProjectHashSuffix()
    {
        // Compute a short hash of the content root path to differentiate between multiple AppHost projects with similar resource names
        var suffix = _configuration["AppHost:Sha256"]!.Substring(0, RandomNameSuffixLength).ToLowerInvariant();
        return suffix;
    }

    public static string GetObjectNameForResource(IResource resource, DcpOptions options, string suffix = "")
    {
        if (resource.TryGetLastAnnotation<ContainerNameAnnotation>(out var containerNameAnnotation))
        {
            // If an explicit container name is provided, use it without any postfix
            return containerNameAnnotation.Name;
        }

        static string maybeWithSuffix(string s, string localSuffix, string? globalSuffix)
            => (string.IsNullOrWhiteSpace(localSuffix), string.IsNullOrWhiteSpace(globalSuffix)) switch
            {
                (true, true) => s,
                (false, true) => $"{s}-{localSuffix}",
                (true, false) => $"{s}-{globalSuffix}",
                (false, false) => $"{s}-{localSuffix}-{globalSuffix}"
            };
        return maybeWithSuffix(resource.Name, suffix, options.ResourceNameSuffix);
    }
}
