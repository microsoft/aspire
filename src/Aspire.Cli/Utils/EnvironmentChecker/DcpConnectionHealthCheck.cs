// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks whether the bundled Developer Control Plane can be reached with its generated kubeconfig.
/// </summary>
internal sealed class DcpConnectionHealthCheck(
    ILayoutDiscovery layoutDiscovery,
    IDcpConnectionTester connectionTester,
    CliExecutionContext executionContext,
    ILogger<DcpConnectionHealthCheck> logger) : IEnvironmentCheck
{
    internal const string BundleCheckName = "dcp-bundle";
    internal const string ConnectionCheckName = "dcp-connection";
    internal const string EphemeralCertificateCheckName = "dcp-ephemeral-certificate";
    internal const string DeveloperCertificateCheckName = "dcp-developer-certificate";

    public int Order => 45; // DCP process checks are more expensive than local prerequisite probes.

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dcpDirectory = layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, executionContext.WorkingDirectory.FullName);
            if (string.IsNullOrWhiteSpace(dcpDirectory))
            {
                logger.LogDebug("Skipping DCP connection health checks because no Aspire bundle layout was discovered.");
                return [new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Aspire,
                    Name = BundleCheckName,
                    Status = EnvironmentCheckStatus.Warning,
                    Message = "DCP bundle not found; skipping DCP connection health checks",
                    Details = "The running Aspire CLI is not associated with a bundle layout that contains DCP."
                }];
            }

            var dcpExecutablePath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
            if (!File.Exists(dcpExecutablePath))
            {
                return [new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Aspire,
                    Name = BundleCheckName,
                    Status = EnvironmentCheckStatus.Fail,
                    Message = "DCP executable not found",
                    Details = $"Expected DCP at '{dcpExecutablePath}'."
                }];
            }

            var ephemeralCertificateResult = await connectionTester.TestConnectionAsync(
                dcpDirectory,
                DcpConnectionSecurityMode.EphemeralCertificate,
                cancellationToken);

            var developerCertificateResult = await connectionTester.TestConnectionAsync(
                dcpDirectory,
                DcpConnectionSecurityMode.DeveloperCertificate,
                cancellationToken);

            return
            [
                ToEnvironmentCheckResult(ephemeralCertificateResult),
                ToEnvironmentCheckResult(developerCertificateResult)
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking DCP connection health.");
            return [new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Aspire,
                Name = ConnectionCheckName,
                Status = EnvironmentCheckStatus.Fail,
                Message = "Failed to check DCP connection health",
                Details = ex.Message
            }];
        }
    }

    private static EnvironmentCheckResult ToEnvironmentCheckResult(DcpConnectionTestResult result)
    {
        return new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Aspire,
            Name = result.Mode switch
            {
                DcpConnectionSecurityMode.EphemeralCertificate => EphemeralCertificateCheckName,
                DcpConnectionSecurityMode.DeveloperCertificate => DeveloperCertificateCheckName,
                _ => ConnectionCheckName
            },
            Status = result.Status,
            Message = result.Message,
            Details = result.Details,
            Fix = result.Fix
        };
    }
}
