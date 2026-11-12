// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Semver;
using static Aspire.Hosting.Resources.MessageStrings;

namespace Aspire.Hosting;

#pragma warning disable ASPIRECOMMAND001, ASPIREEXTENSION001 // Shared implementation of experimental tool APIs.

/// <summary>
/// Implements tool execution and metadata for both canonical and legacy resource contracts.
/// </summary>
internal static class DotnetToolHelpers
{
    internal const string ArgumentSeparator = "--";

    internal static IResourceBuilder<T> AddTool<T>(
        IDistributedApplicationBuilder builder,
        T resource,
        Action<CommandLineArgsCallbackContext> buildArguments,
        Func<T, IEnumerable<ResourcePropertySnapshot>> snapshotProperties,
        Func<RequiredCommandValidationContext, string, Task<RequiredCommandValidationResult>> validateSdk)
        where T : ExecutableResource
    {
        return builder.AddResource(resource)
            // ExecutableResource stores the constructor value verbatim. Normalize the default "."
            // against the AppHost before SDK selection so an unrelated process-directory global.json
            // cannot change whether this tool satisfies the SDK prerequisite.
            .WithWorkingDirectory(resource.WorkingDirectory)
            .WithInitialState(new CustomResourceSnapshot { ResourceType = KnownResourceTypes.Tool, Properties = [] })
            .WithIconName("Toolbox")
            .WithCommand("dotnet")
            .WithLaunchToolArgs(buildArguments, showInCommandLine: false)
            .WithRequiredCommand("dotnet", context => validateSdk(context, resource.WorkingDirectory))
            .OnBeforeResourceStarted(async (current, evt, _) =>
            {
                var properties = snapshotProperties(current).ToArray();
                if (properties.Length > 0)
                {
                    await evt.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(current, snapshot => snapshot with
                    {
                        Properties = snapshot.Properties.SetResourcePropertyRange(properties)
                    }).ConfigureAwait(false);
                }
            });
    }

    internal static void AddArguments(
        CommandLineArgsCallbackContext context,
        string packageId,
        string? version,
        bool prerelease,
        IReadOnlyList<string> sources,
        bool ignoreExistingFeeds,
        bool ignoreFailedSources)
    {
        context.Args.Add("tool");
        context.Args.Add("exec");
        context.Args.Add(packageId);
        foreach (var source in sources)
        {
            context.Args.Add(ignoreExistingFeeds ? "--source" : "--add-source");
            context.Args.Add(source);
        }

        if (ignoreFailedSources)
        {
            context.Args.Add("--ignore-failed-sources");
        }

        if (version is not null)
        {
            context.Args.Add("--version");
            context.Args.Add(version);
        }
        else if (prerelease)
        {
            context.Args.Add("--prerelease");
        }

        context.Args.Add("--yes");
        context.Args.Add(ArgumentSeparator);
    }

    internal static IEnumerable<ResourcePropertySnapshot> CreateSnapshotProperties(string packageId, string? version)
    {
        yield return new(KnownProperties.Tool.Package, packageId)
        {
            DisplayName = ResourcePropertyToolPackageDisplayName,
            IsHighlighted = true,
            SortOrder = 0
        };
        yield return new(KnownProperties.Tool.Version, version)
        {
            DisplayName = ResourcePropertyToolVersionDisplayName,
            IsHighlighted = true,
            SortOrder = 1
        };
        yield return new(KnownProperties.Resource.Source, packageId);
    }

    internal static RequiredCommandValidationResult ValidateSdkVersion(SemVersion? version, string resourceType)
        => version?.Major < 10
            ? RequiredCommandValidationResult.Failure($"{resourceType} requires dotnet SDK 10 or higher to run. Detected version: {version}")
            : RequiredCommandValidationResult.Success();
}
