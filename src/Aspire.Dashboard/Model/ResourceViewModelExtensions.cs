// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Model;

internal static class ResourceViewModelExtensions
{
    /// <summary>
    /// Converts the resource properties to a dictionary of string values.
    /// This is used to provide a consistent interface for code that works with both
    /// ResourceViewModel (Dashboard) and ResourceSnapshot (CLI).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> GetPropertiesAsDictionary(this ResourceViewModel resource)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (key, property) in resource.Properties)
        {
            if (property.Value.TryConvertToString(out var stringValue))
            {
                result[key] = stringValue;
            }
        }

        return result;
    }

    public static bool IsContainer(this ResourceViewModel resource)
    {
        return string.Equals(resource.ResourceType, KnownResourceTypes.Container, StringComparisons.ResourceType);
    }

    public static bool IsProject(this ResourceViewModel resource)
    {
        return string.Equals(resource.ResourceType, KnownResourceTypes.Project, StringComparisons.ResourceType);
    }

    public static bool IsTool(this ResourceViewModel resource)
    {
        return string.Equals(resource.ResourceType, KnownResourceTypes.Tool, StringComparisons.ResourceType);
    }

    public static bool IsExecutable(this ResourceViewModel resource, bool allowSubtypes)
    {
        if (string.Equals(resource.ResourceType, KnownResourceTypes.Executable, StringComparisons.ResourceType))
        {
            return true;
        }

        if (allowSubtypes)
        {
            return string.Equals(resource.ResourceType, KnownResourceTypes.Project, StringComparisons.ResourceType);
        }

        return false;
    }

    public static bool TryGetExitCode(this ResourceViewModel resource, out int exitCode)
    {
        return resource.TryGetCustomDataInt(KnownProperties.Resource.ExitCode, out exitCode);
    }

    public static bool TryGetContainerImage(this ResourceViewModel resource, [NotNullWhen(returnValue: true)] out string? containerImage)
    {
        return resource.TryGetCustomDataString(KnownProperties.Container.Image, out containerImage);
    }

    public static bool TryGetProjectPath(this ResourceViewModel resource, [NotNullWhen(returnValue: true)] out string? projectPath)
    {
        return resource.TryGetCustomDataString(KnownProperties.Project.Path, out projectPath);
    }
    
    public static bool TryGetToolPackage(this ResourceViewModel resource, [NotNullWhen(returnValue: true)] out string? projectPath)
    {
        return resource.TryGetCustomDataString(KnownProperties.Tool.Package, out projectPath);
    }

    public static bool TryGetExecutablePath(this ResourceViewModel resource, [NotNullWhen(returnValue: true)] out string? executablePath)
    {
        return resource.TryGetCustomDataString(KnownProperties.Executable.Path, out executablePath);
    }

    public static bool TryGetExecutableArguments(this ResourceViewModel resource, out ImmutableArray<string> arguments)
    {
        return resource.TryGetCustomDataStringArray(KnownProperties.Executable.Args, out arguments);
    }

    public static bool TryGetAppArgs(this ResourceViewModel resource, out ImmutableArray<string> arguments)
    {
        return resource.TryGetCustomDataStringArray(KnownProperties.Resource.AppArgs, out arguments);
    }

    public static bool TryGetAppArgsSensitivity(this ResourceViewModel resource, out ImmutableArray<bool> argParams)
    {
        return resource.TryGetCustomDataBoolArray(KnownProperties.Resource.AppArgsSensitivity, out argParams);
    }

    /// <summary>
    /// Returns <c>true</c> if the resource has an interactive terminal session available.
    /// </summary>
    public static bool HasTerminal(this ResourceViewModel resource)
    {
        return resource.Properties.ContainsKey(KnownProperties.Terminal.Enabled);
    }

    /// <summary>
    /// Tries to get the per-replica terminal info: the stable replica index and the
    /// total replica count for the parent resource. Both values are stamped onto
    /// each replica snapshot by the AppHost when the resource has
    /// <c>WithTerminal()</c> applied. The pair is sufficient for the dashboard to
    /// build a <c>?resource=&lt;name&gt;&amp;replica=&lt;index&gt;</c> URL that the
    /// terminal WebSocket proxy can resolve to a per-replica HMP v1 producer
    /// socket without exposing the socket path to the browser.
    /// </summary>
    public static bool TryGetTerminalReplicaInfo(this ResourceViewModel resource, out int replicaIndex, out int replicaCount)
    {
        replicaIndex = 0;
        replicaCount = 0;

        if (!resource.TryGetCustomDataString(KnownProperties.Terminal.ReplicaIndex, out var indexString) ||
            !int.TryParse(indexString, NumberStyles.Integer, CultureInfo.InvariantCulture, out replicaIndex))
        {
            return false;
        }

        if (!resource.TryGetCustomDataString(KnownProperties.Terminal.ReplicaCount, out var countString) ||
            !int.TryParse(countString, NumberStyles.Integer, CultureInfo.InvariantCulture, out replicaCount))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tries to get the per-replica consumer UDS path stamped onto the snapshot
    /// by the AppHost. Used by the dashboard's terminal WebSocket proxy to
    /// connect a local HMP v1 client to the terminal host. Returns <c>false</c>
    /// for resources that don't have a terminal or for snapshots from older
    /// AppHost builds that don't stamp this property.
    /// </summary>
    public static bool TryGetTerminalConsumerUdsPath(this ResourceViewModel resource, [NotNullWhen(returnValue: true)] out string? consumerUdsPath)
    {
        return resource.TryGetCustomDataString(KnownProperties.Terminal.ConsumerUdsPath, out consumerUdsPath);
    }

    private static bool TryGetCustomDataString(this ResourceViewModel resource, string key, [NotNullWhen(returnValue: true)] out string? s)
    {
        if (resource.Properties.TryGetValue(key, out var property) && property.Value.TryConvertToString(out var valueString))
        {
            s = valueString;
            return true;
        }

        s = null;
        return false;
    }

    private static bool TryGetCustomDataStringArray(this ResourceViewModel resource, string key, out ImmutableArray<string> strings)
    {
        if (resource.Properties.TryGetValue(key, out var property) && property is { Value: { ListValue: not null } value })
        {
            var builder = ImmutableArray.CreateBuilder<string>(value.ListValue.Values.Count);

            foreach (var element in value.ListValue.Values)
            {
                if (!element.TryConvertToString(out var elementString))
                {
                    strings = default;
                    return false;
                }

                builder.Add(elementString);
            }

            strings = builder.MoveToImmutable();
            return true;
        }

        strings = default;
        return false;
    }

    private static bool TryGetCustomDataBoolArray(this ResourceViewModel resource, string key, out ImmutableArray<bool> bools)
    {
        if (resource.Properties.TryGetValue(key, out var property) && property is { Value: { ListValue: not null } value })
        {
            var builder = ImmutableArray.CreateBuilder<bool>(value.ListValue.Values.Count);

            foreach (var element in value.ListValue.Values)
            {
                if (!element.HasNumberValue)
                {
                    bools = default;
                    return false;
                }

                builder.Add(Convert.ToBoolean(element.NumberValue));
            }

            bools = builder.MoveToImmutable();
            return true;
        }

        bools = default;
        return false;
    }

    private static bool TryGetCustomDataInt(this ResourceViewModel resource, string key, out int i)
    {
        if (resource.Properties.TryGetValue(key, out var property) && property.Value.TryConvertToInt(out i))
        {
            return true;
        }

        i = 0;
        return false;
    }
}
