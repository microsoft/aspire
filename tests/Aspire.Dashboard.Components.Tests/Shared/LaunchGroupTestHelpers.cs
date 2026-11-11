// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.ServiceClient;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class LaunchGroupTestHelpers
{
    public static LaunchGroupInformation CreateInformation(
        string? selectedGroup = null,
        IReadOnlyList<string>? groups = null,
        IReadOnlyList<LaunchGroupResourceInfo>? resources = null)
    {
        var information = new LaunchGroupInformation();
        if (selectedGroup is not null)
        {
            information.SelectedGroup = selectedGroup;
        }
        information.Groups.AddRange(groups ?? []);
        information.Resources.AddRange(resources ?? []);

        return information;
    }

    public static LaunchGroupResourceInfo CreateResource(
        string name,
        IReadOnlyList<string>? groups = null,
        string? inheritedFrom = null,
        bool explicitStart = false,
        LaunchGroupExclusionReason exclusionReason = LaunchGroupExclusionReason.None,
        LaunchGroupBuildType buildType = LaunchGroupBuildType.NotCoordinated)
    {
        var resource = new LaunchGroupResourceInfo
        {
            Name = name,
            ExplicitStart = explicitStart,
            ExclusionReason = exclusionReason,
            BuildType = buildType
        };
        resource.Groups.AddRange(groups ?? []);
        if (inheritedFrom is not null)
        {
            resource.InheritedFrom = inheritedFrom;
        }

        return resource;
    }
}

internal sealed class LaunchGroupTestDashboardClient : TestDashboardClient, IDashboardClient
{
    public LaunchGroupInformationViewModel? LaunchGroups { get; }

    public LaunchGroupTestDashboardClient(
        LaunchGroupInformation information,
        IList<ResourceViewModel> initialResources,
        Func<Channel<IReadOnlyList<ResourceViewModelChange>>> resourceChannelProvider,
        string? applicationName = null,
        Func<string, Channel<IReadOnlyList<ResourceLogLine>>>? consoleLogsChannelProvider = null,
        Func<string, string, CommandViewModel, ExecuteResourceCommandOptions, CancellationToken, Task<ResourceCommandResponseViewModel>>? executeResourceCommand = null)
        : base(
            isEnabled: true,
            applicationName: applicationName,
            consoleLogsChannelProvider: consoleLogsChannelProvider,
            resourceChannelProvider: resourceChannelProvider,
            executeResourceCommand: executeResourceCommand,
            initialResources: initialResources)
    {
        LaunchGroups = LaunchGroupInformationViewModel.Create(information);
        foreach (var resource in initialResources)
        {
            LaunchGroups.ApplyToResource(resource);
        }
    }
}
