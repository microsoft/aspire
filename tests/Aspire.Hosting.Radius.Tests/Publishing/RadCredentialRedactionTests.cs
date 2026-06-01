// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class RadCredentialRedactionTests
{
    [Fact]
    public void RedactSecretArgs_RedactsValuesAfterSecretFlags_LeavesOthersAlone()
    {
        var args = new[]
        {
            "credential", "register", "azure-sp",
            "--name", "aspire-radius-azure",
            "--tenant-id", "tenant-value",
            "--client-id", "client-value",
            "--client-secret", "PLAINTEXT-SECRET",
        };
        var secretFlags = new HashSet<string>(StringComparer.Ordinal) { "--client-secret" };

        var result = RadCredentialRegisterStep.RedactSecretArgs(args, secretFlags);

        Assert.Equal("PLAINTEXT-SECRET", args[^1]);
        Assert.Equal("***", result[^1]);
        Assert.DoesNotContain("PLAINTEXT-SECRET", result);
        Assert.Contains("--client-secret", result);
        Assert.Contains("tenant-value", result);
        Assert.Contains("client-value", result);
    }

    [Fact]
    public void RedactSecretArgs_RedactsMultipleSecretFlags()
    {
        var args = new[]
        {
            "credential", "register", "aws-access-key",
            "--name", "aspire-radius-aws",
            "--access-key-id", "AKIAEXAMPLE",
            "--secret-access-key", "SUPER-SECRET",
        };
        var secretFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "--access-key-id", "--secret-access-key",
        };

        var result = RadCredentialRegisterStep.RedactSecretArgs(args, secretFlags);

        Assert.DoesNotContain("AKIAEXAMPLE", result);
        Assert.DoesNotContain("SUPER-SECRET", result);
        Assert.Equal(2, result.Count(x => x == "***"));
    }

    [Fact]
    public void RedactSecretArgs_NoSecretFlagsConfigured_ReturnsArgsVerbatim()
    {
        var args = new[] { "credential", "register", "aws-irsa", "--name", "n", "--iam-role", "arn:..." };
        var secretFlags = new HashSet<string>(StringComparer.Ordinal);

        var result = RadCredentialRegisterStep.RedactSecretArgs(args, secretFlags);

        Assert.Equal(args, result);
    }
}
