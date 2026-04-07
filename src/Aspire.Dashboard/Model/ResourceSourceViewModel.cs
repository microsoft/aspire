// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Aspire.Shared.Model;

namespace Aspire.Dashboard.Model;

public record LaunchArgument(string Value, bool IsShown);

internal sealed record ResourceSourceViewModel(string value, List<LaunchArgument>? contentAfterValue, string valueToVisualize, string tooltip)
{
    public string Value { get; } = value;
    public List<LaunchArgument>? ContentAfterValue { get; } = contentAfterValue;
    public string ValueToVisualize { get; } = valueToVisualize;
    public string Tooltip { get; } = tooltip;

    internal static ResourceSourceViewModel? GetSourceViewModel(ResourceViewModel resource)
    {
        var properties = resource.GetPropertiesAsDictionary();

        var source = ResourceSource.GetSourceModel(resource.ResourceType, properties);
        if (source is null)
        {
            return null;
        }

        var commandLineInfo = GetCommandLineInfo(resource);
        if (commandLineInfo is null)
        {
            return new ResourceSourceViewModel(
                value: source.Value,
                contentAfterValue: null,
                valueToVisualize: source.OriginalValue,
                tooltip: source.OriginalValue);
        }

        return new ResourceSourceViewModel(
            value: source.Value,
            contentAfterValue: commandLineInfo.Arguments,
            valueToVisualize: $"{source.OriginalValue} {commandLineInfo.ArgumentsString}",
            tooltip: $"{source.OriginalValue} {commandLineInfo.TooltipString}");
    }

    private static CommandLineInfo? GetCommandLineInfo(ResourceViewModel resourceViewModel)
    {
        // If the resource contains launch arguments, these project arguments should be shown in place of all executable arguments,
        // which include args added by the app host
        if (resourceViewModel.TryGetAppArgs(out var launchArguments))
        {
            if (launchArguments.IsDefaultOrEmpty)
            {
                return null;
            }

            var argumentsString = string.Join(" ", launchArguments);
            if (resourceViewModel.TryGetAppArgsSensitivity(out var areArgumentsSensitive))
            {
                var arguments = launchArguments
                    .Select((arg, i) => new LaunchArgument(arg, IsShown: !areArgumentsSensitive[i]))
                    .ToList();

                return new CommandLineInfo(
                    Arguments: arguments,
                    ArgumentsString: argumentsString,
                    TooltipString: string.Join(" ", arguments.Select(arg => arg.IsShown
                        ? arg.Value
                        : DashboardUIHelpers.GetMaskingText(6).Text)));
            }

            return new CommandLineInfo(Arguments: launchArguments.Select(arg => new LaunchArgument(arg, true)).ToList(), ArgumentsString: argumentsString, TooltipString: argumentsString);
        }

        if (resourceViewModel.TryGetExecutableArguments(out var executableArguments) && !resourceViewModel.IsProject())
        {
            var arguments = executableArguments.IsDefaultOrEmpty ? [] : executableArguments.Select(arg => new LaunchArgument(arg, true)).ToList();
            var argumentsString = string.Join(" ", executableArguments);

            return new CommandLineInfo(Arguments: arguments, ArgumentsString: argumentsString, TooltipString: argumentsString);
        }

        return null;
    }

    private record CommandLineInfo(List<LaunchArgument> Arguments, string ArgumentsString, string TooltipString);
}
