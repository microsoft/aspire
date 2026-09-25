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
            "credential", "register", "azure", "sp",
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
            "credential", "register", "aws", "access-key",
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
        var args = new[] { "credential", "register", "aws", "irsa", "--iam-role", "arn:..." };
        var secretFlags = new HashSet<string>(StringComparer.Ordinal);

        var result = RadCredentialRegisterStep.RedactSecretArgs(args, secretFlags);

        Assert.Equal(args, result);
    }

    [Fact]
    public void ExtractSecretValues_ReturnsValuesFollowingSecretFlags()
    {
        var args = new[]
        {
            "credential", "register", "azure", "sp",
            "--tenant-id", "tenant-value",
            "--client-secret", "PLAINTEXT-SECRET",
        };
        var secretFlags = new HashSet<string>(StringComparer.Ordinal) { "--client-secret" };

        var values = RadCredentialRegisterStep.ExtractSecretValues(args, secretFlags);

        Assert.Equal(new[] { "PLAINTEXT-SECRET" }, values);
    }

    [Fact]
    public void ExtractSecretValues_SkipsEmptyValues()
    {
        var args = new[] { "--client-secret", "" };
        var secretFlags = new HashSet<string>(StringComparer.Ordinal) { "--client-secret" };

        var values = RadCredentialRegisterStep.ExtractSecretValues(args, secretFlags);

        Assert.Empty(values);
    }

    [Fact]
    public void RedactSecretValues_ScrubsSecretFromText_LeavesOtherTextAlone()
    {
        var text = "Error: invalid value 'PLAINTEXT-SECRET' for flag --client-secret";

        var result = RadCredentialRegisterStep.RedactSecretValues(text, new[] { "PLAINTEXT-SECRET" });

        Assert.DoesNotContain("PLAINTEXT-SECRET", result);
        Assert.Contains("***", result);
        Assert.Contains("--client-secret", result);
    }

    [Fact]
    public void RedactSecretValues_EmptySecretValue_IsNoOp()
    {
        var text = "some error text";

        var result = RadCredentialRegisterStep.RedactSecretValues(text, new[] { "" });

        Assert.Equal(text, result);
    }

    [Fact]
    public void RedactSecretValues_NoSecretValues_ReturnsTextVerbatim()
    {
        var text = "some error text";

        var result = RadCredentialRegisterStep.RedactSecretValues(text, Array.Empty<string>());

        Assert.Equal(text, result);
    }

    [Fact]
    public void RedactSecretValues_OverlappingSecrets_RedactsLongestFirst_NoRemainderLeaks()
    {
        var text = "value ABCDEF was rejected";

        var result = RadCredentialRegisterStep.RedactSecretValues(text, new[] { "ABC", "ABCDEF" });

        Assert.DoesNotContain("ABC", result);
        Assert.DoesNotContain("DEF", result);
        Assert.Equal("value *** was rejected", result);
    }
}
