// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Selects only log subresources supported and advertised by the observed workload.
/// </summary>
internal static class ResourceLogSources
{
    public static IEnumerable<string> GetSources(CustomResource resource)
    {
        var v2 = resource.ApiVersion == Model.V2.Dcp.GroupVersion.ToString();
        if (!v2 || resource is Model.V2.LogicalContainer { Status.StartupLogs.Available: true })
        {
            yield return Logs.StreamTypeStartupStdErr;
            yield return Logs.StreamTypeStartupStdOut;
        }
        var runtime = resource switch
        {
            Model.V2.LogicalProcess p => p.Status?.RuntimeLogs?.Available == true,
            Model.V2.LogicalContainer c => c.Status?.RuntimeLogs?.Available == true,
            Model.V2.LogicalContainerExec e => e.Status?.RuntimeLogs?.Available == true,
            Executable { Spec.Terminal: not null } => false,
            _ => !v2
        };
        if (runtime)
        {
            yield return Logs.StreamTypeStdOut;
            yield return Logs.StreamTypeStdErr;
        }
        yield return Logs.StreamTypeSystem;
    }

    public static string GetIdentity(CustomResource resource, string source)
    {
        var capture = resource switch
        {
            Model.V2.LogicalProcess p => p.Status?.RuntimeLogs,
            Model.V2.LogicalContainer c when source is Logs.StreamTypeStartupStdOut or Logs.StreamTypeStartupStdErr => c.Status?.StartupLogs,
            Model.V2.LogicalContainer c => c.Status?.RuntimeLogs,
            Model.V2.LogicalContainerExec e => e.Status?.RuntimeLogs,
            _ => null
        };
        return source == Logs.StreamTypeSystem
            ? $"{resource.Metadata.Uid}/{source}"
            : $"{resource.Metadata.Uid}/{capture?.Name}/{capture?.Uid}/{capture?.ContainerId}/{source}";
    }
}
