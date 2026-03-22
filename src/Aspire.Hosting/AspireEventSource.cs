// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

using System.Diagnostics.Tracing;
using Aspire.Hosting.Dcp;

[EventSource(Name = "Microsoft-Aspire-Hosting")]
internal sealed class AspireEventSource : EventSource
{
    public static readonly AspireEventSource Instance = new AspireEventSource();

    [Event(1, Level = EventLevel.Informational, Message = "DCP host is starting...")]
    public void DcpHostStartupStart()
    {
        if (IsEnabled())
        {
            WriteEvent(1);
        }
    }

    [Event(2, Level = EventLevel.Informational, Message = "DCP host has started")]
    public void DcpHostStartupStop()
    {
        if (IsEnabled())
        {
            WriteEvent(2);
        }
    }

    [Event(3, Level = EventLevel.Informational, Message = "Container runtime health check is starting...")]
    public void ContainerRuntimeHealthCheckStart()
    {
        if (IsEnabled())
        {
            WriteEvent(3);
        }
    }

    [Event(4, Level = EventLevel.Informational, Message = "Container runtime health check completed")]
    public void ContainerRuntimeHealthCheckStop()
    {
        if (IsEnabled())
        {
            WriteEvent(4);
        }
    }

    [Event(5, Level = EventLevel.Informational, Message = "DistributedApplication build is starting...")]
    public void DistributedApplicationBuildStart()
    {
        if (IsEnabled())
        {
            WriteEvent(5);
        }
    }

    [Event(6, Level = EventLevel.Informational, Message = "DistributedApplication build completed")]
    public void DistributedApplicationBuildStop()
    {
        if (IsEnabled())
        {
            WriteEvent(6);
        }
    }

    [Event(7, Level = EventLevel.Informational, Message = "DCP API server is starting...")]
    public void DcpApiServerLaunchStart()
    {
        if (IsEnabled())
        {
            WriteEvent(7);
        }
    }

    [Event(8, Level = EventLevel.Informational, Message = "DCP API server has started")]
    public void DcpApiServerLaunchStop()
    {
        if (IsEnabled())
        {
            WriteEvent(8);
        }
    }

    [Event(9, Level = EventLevel.Informational, Message = "DCP logging socket is being created...")]
    public void DcpLogSocketCreateStart()
    {
        if (IsEnabled())
        {
            WriteEvent(9);
        }
    }

    [Event(10, Level = EventLevel.Informational, Message = "DCP logging socket has been created")]
    public void DcpLogSocketCreateStop()
    {
        if (IsEnabled())
        {
            WriteEvent(10);
        }
    }

    [Event(11, Level = EventLevel.Informational, Message = "A process is starting...")]
    public void ProcessLaunchStart(string executablePath, string arguments)
    {
        if (IsEnabled())
        {
            WriteEvent(11, executablePath, arguments);
        }
    }

    [Event(12, Level = EventLevel.Informational, Message = "Process has been started")]
    public void ProcessLaunchStop(string executablePath, string arguments)
    {
        if (IsEnabled())
        {
            WriteEvent(12, executablePath, arguments);
        }
    }

    [Event(13, Level = EventLevel.Informational, Message = "DCP API call starting...")]
    public void DcpApiCallStart(DcpApiOperationType operationType, string resourceType)
    {
        if (IsEnabled())
        {
            WriteEvent(13, operationType, resourceType);
        }
    }

    [Event(14, Level = EventLevel.Informational, Message = "DCP API call completed")]
    public void DcpApiCallStop(DcpApiOperationType operationType, string resourceType)
    {
        if (IsEnabled())
        {
            WriteEvent(14, operationType, resourceType);
        }
    }

    [Event(15, Level = EventLevel.Informational, Message = "DCP API call is being retried...")]
    public void DcpApiCallRetry(DcpApiOperationType operationType, string resourceType)
    {
        if (IsEnabled())
        {
            WriteEvent(15, operationType, resourceType);
        }
    }

    [Event(16, Level = EventLevel.Error, Message = "DCP API call timed out")]
    public void DcpApiCallTimeout(DcpApiOperationType operationType, string resourceType)
    {
        if (IsEnabled())
        {
            WriteEvent(16, operationType, resourceType);
        }
    }

    [Event(17, Level = EventLevel.Informational, Message = "DCP application model creation starting...")]
    public void DcpModelCreationStart()
    {
        if (IsEnabled())
        {
            WriteEvent(17);
        }
    }

    [Event(18, Level = EventLevel.Informational, Message = "DCP application model creation completed")]
    public void DcpModelCreationStop()
    {
        if (IsEnabled())
        {
            WriteEvent(18);
        }
    }

    [Event(19, Level = EventLevel.Informational, Message = "Starting DCP object set creation...")]
    public void DcpObjectSetCreationStart(string kind, int count)
    {
        if (IsEnabled())
        {
            WriteEvent(19, kind, count);
        }
    }

    [Event(20, Level = EventLevel.Informational, Message = "DCP object set creation completed")]
    public void DcpObjectSetCreationStop(string kind, int count)
    {
        if (IsEnabled())
        {
            WriteEvent(20, kind, count);
        }
    }

    [Event(21, Level = EventLevel.Informational, Message = "Creating DCP object...")]
    public void DcpObjectCreationStart(string kind, string resourceName)
    {
        if (IsEnabled())
        {
            WriteEvent(21, kind, resourceName);
        }
    }

    [Event(22, Level = EventLevel.Informational, Message = "DCP object creation completed")]
    public void DcpObjectCreationStop(string kind, string resourceName)
    {
        if (IsEnabled())
        {
            WriteEvent(22, kind, resourceName);
        }
    }

    [Event(23, Level = EventLevel.Informational, Message = "Starting to wait for DCP Service address allocation...")]
    public void DcpServiceAddressAllocationStart(int serviceCount)
    {
        if (IsEnabled())
        {
            WriteEvent(23, serviceCount);
        }
    }

    [Event(24, Level = EventLevel.Informational, Message = "DCP Service address allocation completed")]
    public void DcpServiceAddressAllocationStop(int serviceCount)
    {
        if (IsEnabled())
        {
            WriteEvent(24, serviceCount);
        }
    }

    [Event(25, Level = EventLevel.Informational, Message = "DCP resource cleanup starting...")]
    public void DcpResourceCleanupStart()
    {
        if (IsEnabled())
        {
            WriteEvent(25);
        }
    }

    [Event(26, Level = EventLevel.Informational, Message = "DCP resource cleanup completed")]
    public void DcpResourceCleanupStop()
    {
        if (IsEnabled())
        {
            WriteEvent(26);
        }
    }

    [Event(27, Level = EventLevel.Informational, Message = "Application before-start hooks running...")]
    public void AppBeforeStartHooksStart()
    {
        if (IsEnabled())
        {
            WriteEvent(27);
        }
    }

    [Event(28, Level = EventLevel.Informational, Message = "Application before-start hooks completed")]
    public void AppBeforeStartHooksStop()
    {
        if (IsEnabled())
        {
            WriteEvent(28);
        }
    }

    [Event(29, Level = EventLevel.Informational, Message = "DCP version check is starting...")]
    public void DcpVersionCheckStart()
    {
        if (IsEnabled())
        {
            WriteEvent(29);
        }
    }

    [Event(30, Level = EventLevel.Informational, Message = "DCP version check completed")]
    public void DcpVersionCheckStop()
    {
        if (IsEnabled())
        {
            WriteEvent(30);
        }
    }

    [Event(33, Level = EventLevel.Informational, Message = "DCP information fetching start...")]
    public void DcpInfoFetchStart(bool forced)
    {
        if (IsEnabled())
        {
            WriteEvent(33, forced);
        }
    }

    [Event(34, Level = EventLevel.Informational, Message = "DCP information fetching completed")]
    public void DcpInfoFetchStop(bool forced)
    {
        if (IsEnabled())
        {
            WriteEvent(34, forced);
        }
    }

    [Event(35, Level = EventLevel.Informational, Message = "DCP Service address allocated")]
    public void DcpServiceAddressAllocated(string serviceName)
    {
        if (IsEnabled())
        {
            WriteEvent(35, serviceName);
        }
    }

    [Event(36, Level = EventLevel.Error, Message = "DCP Service address allocation failed")]
    public void DcpServiceAddressAllocationFailed(string serviceName)
    {
        if (IsEnabled())
        {
            WriteEvent(36, serviceName);
        }
    }

    [Event(37, Level = EventLevel.Informational, Message = "Creating DCP resources for Aspire executable...")]
    public void CreateAspireExecutableResourcesStart(string executableName)
    {
        if (IsEnabled())
        {
            WriteEvent(37, executableName);
        }
    }

    [Event(38, Level = EventLevel.Informational, Message = "Aspire executable resources created")]
    public void CreateAspireExecutableResourcesStop(string executableName)
    {
        if (IsEnabled())
        {
            WriteEvent(38, executableName);
        }
    }

    [Event(39, Level = EventLevel.Informational, Message = "Stopping DCP resource...")]
    public void StopResourceStart(string kind, string resourceName)
    {
        if (IsEnabled())
        {
            WriteEvent(39, kind, resourceName);
        }
    }

    [Event(40, Level = EventLevel.Informational, Message = "DCP resource stopped")]
    public void StopResourceStop(string kind, string resourceName)
    {
        if (IsEnabled())
        {
            WriteEvent(40, kind, resourceName);
        }
    }

    [Event(41, Level = EventLevel.Informational, Message = "Starting DCP resource...")]
    public void StartResourceStart(string kind, string resourceName)
    {
        if (IsEnabled())
        {
            WriteEvent(41, kind, resourceName);
        }
    }

    [Event(42, Level = EventLevel.Informational, Message = "DCP resource started")]
    public void StartResourceStop(string kind, string resourceName)
    {
        if (IsEnabled())
        {
            WriteEvent(42, kind, resourceName);
        }
    }

    [Event(43, Level = EventLevel.Informational, Message = "Development certificate trust check is starting...")]
    public void DevelopmentCertificateTrustCheckStart()
    {
        if (IsEnabled())
        {
            WriteEvent(43);
        }
    }

    [Event(44, Level = EventLevel.Informational, Message = "Development certificate trust check completed")]
    public void DevelopmentCertificateTrustCheckStop()
    {
        if (IsEnabled())
        {
            WriteEvent(44);
        }
    }

    [Event(45, Level = EventLevel.Informational, Message = "DCP Service object preparation starting...")]
    public void DcpServiceObjectPreparationStart()
    {
        if (IsEnabled())
        {
            WriteEvent(45);
        }
    }

    [Event(46, Level = EventLevel.Informational, Message = "DCP Service object preparation completed")]
    public void DcpServiceObjectPreparationStop()
    {
        if (IsEnabled())
        {
            WriteEvent(46);
        }
    }
}
