// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Security.Authentication;
using Aspire.Hosting.Yarp;
using Yarp.ReverseProxy.Configuration;

namespace Aspire.Hosting;

internal class YarpEnvConfigGenerator
{
    private const string Prefix = "REVERSEPROXY__";

    public static void PopulateEnvVariables(Dictionary<string, object> environmentVariables, List<YarpRoute> routes, List<YarpCluster> clusters)
    {
        foreach (var route in routes.Select(r => r.RouteConfig))
        {
            FlattenToEnvVars(environmentVariables, route, $"{Prefix}ROUTES__{route.RouteId}");
            // Hack: YARP throws if RouteConfig.RouterId is populated in the config.
            // YARP will get the RouteId from the config key and populate the value in the RouteConfig itself.
            environmentVariables.Remove($"{Prefix}ROUTES__{route.RouteId}__ROUTEID");
        }

        foreach (var cluster in clusters)
        {
            FlattenToEnvVars(environmentVariables, cluster.ClusterConfig, $"{Prefix}CLUSTERS__{cluster.ClusterConfig.ClusterId}");
            var targets = cluster.GetResolvedTargets();
            for (var i = 0; i < targets.Length; i++)
            {
                environmentVariables[$"{Prefix}CLUSTERS__{cluster.ClusterConfig.ClusterId}__DESTINATIONS__destination{i + 1}__ADDRESS"] = targets[i];
            }
            // Hack: YARP throws if ClusterConfig.ClusterId is populated in the config.
            // YARP will get the ClusterId from the config key and populate the value in the ClusterConfig itself.
            environmentVariables.Remove($"{Prefix}CLUSTERS__{cluster.ClusterConfig.ClusterId}__CLUSTERID");
        }
    }

    private static void FlattenToEnvVars(Dictionary<string, object> environmentVariables, object obj, string prefix)
    {
        if (IsSimple(obj))
        {
            environmentVariables.Add(prefix, obj.ToString() ?? "");
        }
        else if (obj is IReadOnlyDictionary<string, string> dict)
        {
            foreach (var kvp in dict)
            {
                FlattenToEnvVars(environmentVariables, kvp.Value, $"{prefix}__{kvp.Key}");
            }
        }
        else if (obj is IReadOnlyDictionary<string, DestinationConfig> dict2)
        {
            foreach (var kvp in dict2)
            {
                FlattenToEnvVars(environmentVariables, kvp.Value, $"{prefix}__{kvp.Key}");
            }
        }
        else if (obj is IReadOnlyList<object> list)
        {
            var counter = 0;
            foreach (var subValue in list)
            {
                FlattenToEnvVars(environmentVariables, subValue, $"{prefix}__{counter}");
                counter++;
            }
        }
        else if (obj is Version version)
        {
            environmentVariables.Add(prefix, version.ToString());
        }
        else if (obj is SslProtocols sslProtocols)
        {
            var counter = 0;
            foreach (var protocol in Enum.GetValues<SslProtocols>())
            {
                if (protocol != SslProtocols.None)
                {
                    if ((sslProtocols & protocol) == protocol)
                    {
                        environmentVariables.Add($"{prefix}__{counter}", protocol.ToString());
                        counter++;
                    }
                }
            }
        }
        else
        {
            foreach (var property in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var value = property.GetValue(obj);

                if (value == null)
                {
                    continue;
                }
                else
                {
                    var key = $"{prefix}__{property.Name.ToUpperInvariant()}";
                    FlattenToEnvVars(environmentVariables, value, key);
                }
            }
        }
    }

    private static bool IsSimple(object value)
    {
        var type = value.GetType();
        return type.IsPrimitive || type == typeof(string) || type == typeof(decimal);
    }
}
