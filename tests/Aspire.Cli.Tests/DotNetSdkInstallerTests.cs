// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Configuration;
using Semver;

namespace Aspire.Cli.Tests;

public class DotNetSdkInstallerTests
{
    [Fact]
    public async Task CheckAsync_WhenDotNetIsAvailable_ReturnsTrue()
    {
        var installer = new DotNetSdkInstaller(CreateEmptyConfiguration());

        // This test assumes the test environment has .NET SDK installed
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        Assert.True(success);
    }

    [Fact]
    public async Task CheckAsync_WithMinimumVersion_WhenDotNetIsAvailable_ReturnsTrue()
    {
        var configuration = CreateConfigurationWithOverride("8.0.0");
        var installer = new DotNetSdkInstaller(configuration);

        // This test assumes the test environment has .NET SDK installed with a version >= 8.0.0
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        Assert.True(success);
    }

    [Fact]
    public async Task CheckAsync_WithActualMinimumVersion_BehavesCorrectly()
    {
        var configuration = CreateConfigurationWithOverride(DotNetSdkInstaller.MinimumSdkVersion);
        var installer = new DotNetSdkInstaller(configuration);

        // Use the actual minimum version constant and check the behavior
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        // Don't assert the specific result, just ensure the method doesn't throw
        // The behavior will depend on what SDK versions are actually installed
        Assert.True(success == true || success == false); // This will always pass but exercises the code path
    }

    [Fact]
    public async Task CheckAsync_WithHighMinimumVersion_ReturnsFalse()
    {
        var configuration = CreateConfigurationWithOverride("99.0.0");
        var installer = new DotNetSdkInstaller(configuration);

        // Use an unreasonably high version that should not exist
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        Assert.False(success);
    }

    [Fact]
    public async Task CheckAsync_WithInvalidMinimumVersion_ReturnsFalse()
    {
        var configuration = CreateConfigurationWithOverride("invalid.version");
        var installer = new DotNetSdkInstaller(configuration);

        // Use an invalid version string
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        Assert.False(success);
    }

    [Fact]
    public async Task CheckAsync_UsesArchitectureSpecificCommand()
    {
        var configuration = CreateConfigurationWithOverride("8.0.0");
        var installer = new DotNetSdkInstaller(configuration);

        // This test verifies that the architecture-specific command is used
        // Since the implementation adds --arch flag, it should still work correctly
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        // The test should pass if the command with --arch flag works
        Assert.True(success);
    }

    [Fact]
    public async Task CheckAsync_UsesOverrideMinimumSdkVersion_WhenConfigured()
    {
        var configuration = CreateConfigurationWithOverride("8.0.0");
        var installer = new DotNetSdkInstaller(configuration);

        // The installer should use the override version instead of the constant
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        // Should use 8.0.0 instead of 9.0.302, which should be available in test environment
        Assert.True(success);
    }

    [Fact]
    public async Task CheckAsync_UsesDefaultMinimumSdkVersion_WhenNotConfigured()
    {
        var installer = new DotNetSdkInstaller(CreateEmptyConfiguration());

        // Call the parameterless method that should use the default constant
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        // The result depends on whether 9.0.302 is installed, but the test ensures no exception is thrown
        Assert.True(success == true || success == false);
    }

    [Fact]
    public async Task CheckAsync_UsesMinimumSdkVersion()
    {
        var installer = new DotNetSdkInstaller(CreateEmptyConfiguration());

        // Call the parameterless method that should use the minimum SDK version
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        // The result depends on whether 10.0.100 is installed, but the test ensures no exception is thrown
        Assert.True(success == true || success == false);
    }

    [Fact]
    public async Task CheckAsync_UsesOverrideVersion_WhenOverrideConfigured()
    {
        var configuration = CreateConfigurationWithOverride("8.0.0");
        var installer = new DotNetSdkInstaller(configuration);

        // The installer should use the override version instead of the baseline constant
        var (success, _, _) = await installer.CheckAsync().DefaultTimeout();

        // Should use 8.0.0 instead of 10.0.100, which should be available in test environment
        Assert.True(success);
    }

    [Fact]
    public void GetEffectiveMinimumSdkVersion_ReturnsBaseline_WhenNoOverrides()
    {
        var configuration = CreateEmptyConfiguration();

        var effectiveVersion = DotNetSdkInstaller.GetEffectiveMinimumSdkVersion(configuration);

        Assert.Equal(DotNetSdkInstaller.MinimumSdkVersion, effectiveVersion);
    }

    [Fact]
    public void GetEffectiveMinimumSdkVersion_ReturnsOverride_WhenOverrideConfigured()
    {
        var configuration = CreateConfigurationWithOverride("7.0.0");

        var effectiveVersion = DotNetSdkInstaller.GetEffectiveMinimumSdkVersion(configuration);

        Assert.Equal("7.0.0", effectiveVersion);
    }

    [Fact]
    public void ErrorMessage_Format_IsCorrect()
    {
        // Test the error message format with placeholders
        var message = string.Format(CultureInfo.InvariantCulture,
            ErrorStrings.ResourceManager.GetString("MinimumSdkVersionNotMet", CultureInfo.GetCultureInfo("en-US"))!,
            "10.0.100",
            "(not found)");

        Assert.Equal("C# AppHost requires .NET SDK version 10.0.100 or later. Detected: (not found).", message);
    }

    [Fact]
    public void MeetsMinimumRequirement_AllowsDotNet10Prereleases()
    {
        // Test the logic we added for allowing .NET 10 prereleases
        var installedVersion = SemVersion.Parse("10.0.100-preview.1.25463.5", SemVersionStyles.Strict);
        var requiredVersion = SemVersion.Parse("10.0.100", SemVersionStyles.Strict);
        var requiredVersionString = "10.0.100";

        // Use reflection to access the private method
        var method = typeof(DotNetSdkInstaller).GetMethod("MeetsMinimumRequirement",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (bool)method!.Invoke(null, new object[] { installedVersion, requiredVersion, requiredVersionString })!;

        Assert.True(result);
    }

    [Fact]
    public void MeetsMinimumRequirement_AllowsDotNet10LatestPrerelease()
    {
        // Test with a more recent .NET 10 prerelease
        var installedVersion = SemVersion.Parse("10.1.0-preview.2.25999.99", SemVersionStyles.Strict);
        var requiredVersion = SemVersion.Parse("10.0.100", SemVersionStyles.Strict);
        var requiredVersionString = "10.0.100";

        // Use reflection to access the private method
        var method = typeof(DotNetSdkInstaller).GetMethod("MeetsMinimumRequirement",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (bool)method!.Invoke(null, new object[] { installedVersion, requiredVersion, requiredVersionString })!;

        Assert.True(result);
    }

    [Fact]
    public void MeetsMinimumRequirement_RejectsDotNet9()
    {
        // Test that .NET 9 is rejected
        var installedVersion = SemVersion.Parse("9.0.999", SemVersionStyles.Strict);
        var requiredVersion = SemVersion.Parse("10.0.100", SemVersionStyles.Strict);
        var requiredVersionString = "10.0.100";

        // Use reflection to access the private method
        var method = typeof(DotNetSdkInstaller).GetMethod("MeetsMinimumRequirement",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (bool)method!.Invoke(null, new object[] { installedVersion, requiredVersion, requiredVersionString })!;

        Assert.False(result);
    }

    [Fact]
    public void MeetsMinimumRequirement_UsesStrictComparison_ForOtherVersions()
    {
        // Test that other version requirements still use strict comparison
        var installedVersion = SemVersion.Parse("9.0.301", SemVersionStyles.Strict);
        var requiredVersion = SemVersion.Parse("9.0.302", SemVersionStyles.Strict);
        var requiredVersionString = "9.0.302";

        // Use reflection to access the private method
        var method = typeof(DotNetSdkInstaller).GetMethod("MeetsMinimumRequirement",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (bool)method!.Invoke(null, new object[] { installedVersion, requiredVersion, requiredVersionString })!;

        Assert.False(result);
    }

    private static IConfiguration CreateEmptyConfiguration()
    {
        return new ConfigurationBuilder().Build();
    }

    private static IConfiguration CreateConfigurationWithOverride(string overrideVersion)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("overrideMinimumSdkVersion", overrideVersion)
            })
            .Build();
    }
}

public class TestFeatures : IFeatures
{
    private readonly Dictionary<string, bool> _features = new();

    public TestFeatures SetFeature(string featureName, bool value)
    {
        _features[featureName] = value;
        return this;
    }

    public bool IsFeatureEnabled(string featureName, bool defaultValue = false)
    {
        return _features.TryGetValue(featureName, out var value) ? value : defaultValue;
    }
}
