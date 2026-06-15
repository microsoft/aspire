// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes.Tests;

public class HelmVersionValidatorTests
{
    [Theory]
    [InlineData("v4.2.0+gfa15ec0", 4, 2, 0)]
    [InlineData("v4.0.0", 4, 0, 0)]
    [InlineData("v4.5.1+g123abc", 4, 5, 1)]
    [InlineData("v5.0.0", 5, 0, 0)]
    [InlineData("v3.18.0+gb88f836", 3, 18, 0)]
    [InlineData("4.2.0", 4, 2, 0)]
    public void TryParseHelmVersion_ValidOutput_ReturnsTrueAndVersion(string output, int major, int minor, int patch)
    {
        Assert.True(HelmVersionValidator.TryParseHelmVersion(output, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a version")]
    [InlineData("helm: command not found")]
    public void TryParseHelmVersion_InvalidOutput_ReturnsFalse(string output)
    {
        Assert.False(HelmVersionValidator.TryParseHelmVersion(output, out _));
    }

    [Fact]
    public async Task EnsureMinimumVersionAsync_AtMinimum_Passes()
    {
        var runner = new FakeHelmRunner { VersionOutput = "v4.2.0+gfa15ec0" };
        await HelmVersionValidator.EnsureMinimumVersionAsync(runner, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureMinimumVersionAsync_NewerVersion_Passes()
    {
        var runner = new FakeHelmRunner { VersionOutput = "v5.1.0+g111111" };
        await HelmVersionValidator.EnsureMinimumVersionAsync(runner, CancellationToken.None);
    }

    [Theory]
    [InlineData("v4.1.0+gfa15ec0")]
    [InlineData("v4.0.0")]
    [InlineData("v3.18.0+gb88f836")]
    [InlineData("v3.14.4+gb88f836")]
    public async Task EnsureMinimumVersionAsync_TooOld_ThrowsWithDetectedAndRequired(string oldVersion)
    {
        var runner = new FakeHelmRunner { VersionOutput = oldVersion };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HelmVersionValidator.EnsureMinimumVersionAsync(runner, CancellationToken.None));

        // Detected version (without leading 'v' and without build metadata)
        var trimmed = oldVersion.TrimStart('v').Split('+')[0];
        Assert.Contains(trimmed, ex.Message, StringComparison.Ordinal);
        Assert.Contains(HelmVersionValidator.MinimumHelmVersion.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://helm.sh/docs/intro/install/", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureMinimumVersionAsync_UnparseableOutput_ThrowsWithRawOutput()
    {
        var runner = new FakeHelmRunner { VersionOutput = "garbage banner" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HelmVersionValidator.EnsureMinimumVersionAsync(runner, CancellationToken.None));

        Assert.Contains("garbage banner", ex.Message, StringComparison.Ordinal);
        Assert.Contains(HelmVersionValidator.MinimumHelmVersion.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureMinimumVersionAsync_NonZeroExitCode_ThrowsWithInstallHint()
    {
        var runner = new FakeHelmRunner
        {
            VersionOutput = string.Empty,
            VersionExitCode = 1,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HelmVersionValidator.EnsureMinimumVersionAsync(runner, CancellationToken.None));

        Assert.Contains("https://helm.sh/docs/intro/install/", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureMinimumVersionAsync_DoesNotPassClientFlag()
    {
        // Regression: --client was removed in Helm 4 (the minimum version Aspire
        // requires), so passing it makes the validator's own probe fail with
        // "Error: unknown flag: --client" against the very baseline it is meant
        // to validate.
        var runner = new RecordingHelmRunner();
        await HelmVersionValidator.EnsureMinimumVersionAsync(runner, CancellationToken.None);

        Assert.NotNull(runner.LastArguments);
        Assert.DoesNotContain("--client", runner.LastArguments, StringComparison.Ordinal);
        Assert.Contains("version", runner.LastArguments, StringComparison.Ordinal);
        Assert.Contains("--short", runner.LastArguments, StringComparison.Ordinal);
    }

    private sealed class RecordingHelmRunner : IHelmRunner
    {
        public string? LastArguments { get; private set; }

        public Task<int> RunAsync(
            string arguments,
            string? workingDirectory = null,
            Action<string>? onOutputData = null,
            Action<string>? onErrorData = null,
            CancellationToken cancellationToken = default)
        {
            LastArguments = arguments;
            onOutputData?.Invoke("v4.2.0+gfa15ec0");
            return Task.FromResult(0);
        }
    }
}
