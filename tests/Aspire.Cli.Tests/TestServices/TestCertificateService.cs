// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Certificates;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestCertificateService : ICertificateService
{
    public Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new EnsureCertificatesTrustedResult
        {
            EnvironmentVariables = new Dictionary<string, string>(),
            Success = true
        });
    }
}
