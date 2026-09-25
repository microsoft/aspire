// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDcpConnectionChecker : IDcpConnectionChecker
{
    public Func<string, bool, CancellationToken, Task<EnvironmentCheckResult>>? TestConnectionAsyncCallback { get; set; }

    public Task<EnvironmentCheckResult> TestConnectionAsync(string dcpDirectory, bool useDeveloperCertificate, CancellationToken cancellationToken)
    {
        if (TestConnectionAsyncCallback is not null)
        {
            return TestConnectionAsyncCallback(dcpDirectory, useDeveloperCertificate, cancellationToken);
        }

        return Task.FromResult(CreateResult(useDeveloperCertificate, EnvironmentCheckStatus.Pass, $"DCP useDeveloperCertificate={useDeveloperCertificate} connection succeeded"));
    }

    public static EnvironmentCheckResult CreateResult(bool useDeveloperCertificate, EnvironmentCheckStatus status, string message, string? details = null, string? fix = null)
    {
        return new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Aspire,
            Name = useDeveloperCertificate
                ? DcpConnectionHealthCheck.DeveloperCertificateCheckName
                : DcpConnectionHealthCheck.EphemeralCertificateCheckName,
            Status = status,
            Message = message,
            Details = details,
            Fix = fix
        };
    }
}
