// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Resources;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks whether the bundled Developer Control Plane can be reached with its generated kubeconfig.
/// </summary>
internal sealed class DcpConnectionHealthCheck(
    ILayoutDiscovery layoutDiscovery,
    IBundleService bundleService,
    IDcpConnectionChecker connectionTester,
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
        BundleLayoutLease? layoutLease = null;
        try
        {
            // An existing layout wins, which keeps an ASPIRE_DCP_PATH override and an already
            // extracted bundle working exactly as before.
            var dcpDirectory = layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, executionContext.WorkingDirectory.FullName);
            if (string.IsNullOrWhiteSpace(dcpDirectory))
            {
                // Layout discovery only finds a bundle that is already on disk. It used to be extracted
                // by the time doctor ran because bundled NuGet search, which the background CLI update
                // check runs on startup, extracted it before launching aspire-managed. NuGet now runs
                // in-process and no longer does, so on a fresh install extract it here. The lease is
                // held for the whole check because the connection probes below launch DCP from it.
                layoutLease = await bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "doctor", cancellationToken).ConfigureAwait(false);
                dcpDirectory = layoutLease?.Layout.GetDcpPath();
            }

            if (string.IsNullOrWhiteSpace(dcpDirectory))
            {
                logger.LogDebug("Skipping DCP connection health checks because no Aspire bundle layout was discovered.");
                return [new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Aspire,
                    Name = BundleCheckName,
                    Status = EnvironmentCheckStatus.Warning,
                    Message = DoctorCommandStrings.DcpBundleNotFoundMessage,
                    Details = DoctorCommandStrings.DcpBundleNotFoundDetails
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
                    Message = DoctorCommandStrings.DcpExecutableNotFoundMessage,
                    Details = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DcpExecutableNotFoundDetailsFormat, dcpExecutablePath)
                }];
            }

            var ephemeralCertificateTask = connectionTester.TestConnectionAsync(
                dcpDirectory,
                useDeveloperCertificate: false,
                cancellationToken);

            var developerCertificateTask = connectionTester.TestConnectionAsync(
                dcpDirectory,
                useDeveloperCertificate: true,
                cancellationToken);

            var results = await Task.WhenAll(ephemeralCertificateTask, developerCertificateTask).ConfigureAwait(false);
            if (results.All(result => result.Status == EnvironmentCheckStatus.Pass))
            {
                return [new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Aspire,
                    Name = ConnectionCheckName,
                    Status = EnvironmentCheckStatus.Pass,
                    Message = DoctorCommandStrings.DcpConnectionSucceededMessage
                }];
            }

            return results;
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
                Message = DoctorCommandStrings.DcpConnectionCheckFailedMessage,
                Details = ex.Message
            }];
        }
        finally
        {
            layoutLease?.Dispose();
        }
    }

}
