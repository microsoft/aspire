// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class ConfigCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ConfigCommand_WithExtensionMode_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper,
            options =>
            {
                options.ConfigurationCallback += config =>
                {
                    // Enable extension mode for testing
                    config["ASPIRE_EXTENSION_PROMPT_ENABLED"] = "true";
                    config["ASPIRE_EXTENSION_TOKEN"] = "token";
                };

                options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp);
                options.ExtensionBackchannelFactory = sp => new TestExtensionBackchannel();

                options.ConfigurationServiceFactory = _ => new TestConfigurationService();
            });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("config");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ConfigCommandReturnsInvalidCommandExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("config");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task ConfigSetCommand_WithFlatKey_CreatesSimpleProperty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("config set foo bar");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);

        // Verify the settings file was created correctly
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        Assert.True(File.Exists(settingsPath));

        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);
        Assert.Equal("bar", settings["foo"]?.ToString());
    }

    [Fact]
    public async Task ConfigSetCommand_WithDotNotation_CreatesNestedObject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("config set foo.bar baz");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);

        // Verify the settings file was created correctly
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        Assert.True(File.Exists(settingsPath));

        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);
        Assert.True(settings["foo"] is JsonObject);
        var fooObject = settings["foo"]!.AsObject();
        Assert.Equal("baz", fooObject["bar"]?.ToString());
    }

    [Fact]
    public async Task ConfigSetCommand_WithDeepDotNotation_CreatesDeeplyNestedObject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("config set foo.bar.baz hello");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);

        // Verify the settings file was created correctly
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        Assert.True(File.Exists(settingsPath));

        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);

        Assert.True(settings["foo"] is JsonObject);
        var fooObject = settings["foo"]!.AsObject();
        Assert.True(fooObject["bar"] is JsonObject);
        var barObject = fooObject["bar"]!.AsObject();
        Assert.Equal("hello", barObject["baz"]?.ToString());
    }

    [Fact]
    public async Task ConfigSetCommand_ReplacesPrimitiveWithObject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // First set a primitive value
        var result1 = command.Parse("config set foo primitive");
        var exitCode1 = await result1.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode1);

        // Then set a nested value that should replace the primitive
        var result2 = command.Parse("config set foo.bar nested");
        var exitCode2 = await result2.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode2);

        // Verify the primitive was replaced with an object
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);

        Assert.True(settings["foo"] is JsonObject);
        var fooObject = settings["foo"]!.AsObject();
        Assert.Equal("nested", fooObject["bar"]?.ToString());
    }

    [Fact]
    public async Task ConfigGetCommand_WithFlatKey_ReturnsValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services1 = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider1 = services1.BuildServiceProvider();

        var command1 = provider1.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // First set a value
        var setResult = command1.Parse("config set testkey testvalue");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        // Create a new service collection to reload config.
        var services2 = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider2 = services2.BuildServiceProvider();

        var command2 = provider2.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Then get the value
        var getResult = command2.Parse("config get testkey");
        var getExitCode = await getResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, getExitCode);
    }

    [Fact]
    public async Task ConfigGetCommand_WithDotNotation_ReturnsNestedValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services1 = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider1 = services1.BuildServiceProvider();

        var command1 = provider1.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // First set a nested value
        var setResult = command1.Parse("config set level1.level2.level3 nestedvalue");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        var services2 = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider2 = services2.BuildServiceProvider();

        var command2 = provider2.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Then get the nested value
        var getResult = command2.Parse("config get level1.level2.level3");
        var getExitCode = await getResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, getExitCode);
    }

    [Fact]
    public async Task ConfigGetCommand_WithNonExistentKey_ReturnsError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("config get nonexistent.key");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(10, exitCode);
    }

    [Fact]
    public async Task ConfigDeleteCommand_WithFlatKey_RemovesValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // First set a value
        var setResult = command.Parse("config set deletekey deletevalue");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        // Then delete the value
        var deleteResult = command.Parse("config delete deletekey");
        var deleteExitCode = await deleteResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, deleteExitCode);

        // Verify it's deleted
        var getResult = command.Parse("config get deletekey");
        var getExitCode = await getResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(10, getExitCode); // Should return error for missing key
    }

    [Fact]
    public async Task ConfigDeleteCommand_CleansUpEmptyParentObjects()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Set a deeply nested value
        var setResult = command.Parse("config set deep.nested.value test");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        // Delete the nested value
        var deleteResult = command.Parse("config delete deep.nested.value");
        var deleteExitCode = await deleteResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, deleteExitCode);

        // Verify the entire deep.nested structure is cleaned up
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);

        // The deep object should be completely removed since it became empty
        Assert.False(settings.ContainsKey("deep"));
    }

    [Fact]
    public async Task ConfigListCommand_ShowsFlattenedDotNotationKeys()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Set various values
        var setResult1 = command.Parse("config set flatkey flatvalue");
        var setExitCode1 = await setResult1.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode1);

        var setResult2 = command.Parse("config set nested.key nestedvalue");
        var setExitCode2 = await setResult2.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode2);

        var setResult3 = command.Parse("config set deep.nested.key deepvalue");
        var setExitCode3 = await setResult3.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode3);

        // List all configuration
        var listResult = command.Parse("config list");
        var listExitCode = await listResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, listExitCode);
    }

    [Fact]
    public async Task ConfigListCommand_WithoutAllFlag_ShowsHintInsteadOfFeatures()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Set a value so config list has something to display
        var setResult = command.Parse("config set testkey testvalue");
        await setResult.InvokeAsync().DefaultTimeout();

        // List without --all
        var listResult = command.Parse("config list");
        var listExitCode = await listResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, listExitCode);

        var output = string.Join("\n", outputWriter.Logs);
        Assert.Contains("--all", output);
    }

    [Fact]
    public async Task ConfigListCommand_WithAllFlag_ShowsFeatureDetails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Set a value so config list has something to display
        var setResult = command.Parse("config set testkey testvalue");
        await setResult.InvokeAsync().DefaultTimeout();

        // List with --all
        var listResult = command.Parse("config list --all");
        var listExitCode = await listResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, listExitCode);

        var output = string.Join("\n", outputWriter.Logs);
        Assert.Contains("default:", output);
    }

    [Fact]
    public async Task ConfigListCommand_WithoutAllFlag_NoConfig_ShowsHintAboutAllFlag()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // List without --all and with no config set
        var listResult = command.Parse("config list");
        var listExitCode = await listResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, listExitCode);

        var output = string.Join("\n", outputWriter.Logs);
        Assert.Contains("--all", output);
    }

    [Fact]
    public async Task ConfigListCommand_WithAllFlag_NoConfig_ShowsAvailableFeatures()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // List with --all and with no config set
        var listResult = command.Parse("config list --all");
        var listExitCode = await listResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, listExitCode);

        var output = string.Join("\n", outputWriter.Logs);
        Assert.Contains("default:", output);
    }

    [Fact]
    public async Task FeatureFlags_WhenSetToTrue_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(
            workspace,
            outputHelper,
            options => options.EnabledFeatures = new[] { "testFeature" }
            );
        var provider = services.BuildServiceProvider();

        // Set the feature flag to true
        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var setResult = command.Parse($"config set {KnownFeatures.FeaturePrefix}.testFeature true");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        // Check the feature flag
        var featureFlags = provider.GetRequiredService<IFeatures>();
        Assert.True(featureFlags.IsFeatureEnabled("testFeature", defaultValue: false));
    }

    [Fact]
    public async Task FeatureFlags_WhenSetToFalse_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => options.DisabledFeatures = new[] { "testFeature" });
        var provider = services.BuildServiceProvider();

        // Set the feature flag to false
        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var setResult = command.Parse($"config set {KnownFeatures.FeaturePrefix}.testFeature false");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        // Check the feature flag
        var featureFlags = provider.GetRequiredService<IFeatures>();
        Assert.False(featureFlags.IsFeatureEnabled("testFeature", defaultValue: true));
    }

    [Fact]
    public async Task FeatureFlags_WhenSetToInvalidValue_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(
            workspace,
            outputHelper,
            options => options.ConfigurationCallback += confing =>
            {
                confing[$"{KnownFeatures.FeaturePrefix}:testFeature"] = "invalid"; // Set an invalid value
            });
        var provider = services.BuildServiceProvider();

        // Set the feature flag to an invalid value
        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var setResult = command.Parse($"config set {KnownFeatures.FeaturePrefix}.testFeature invalid");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        // Check the feature flag
        var featureFlags = provider.GetRequiredService<IFeatures>();
        Assert.False(featureFlags.IsFeatureEnabled("testFeature", defaultValue: true));
    }

    [Fact]
    public void DeployCommand_IsAlwaysAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Check that deploy command is always available
        var hasDeployCommand = rootCommand.Subcommands.Any(cmd => cmd.Name == "deploy");
        Assert.True(hasDeployCommand);
    }

    [Fact]
    public async Task ShowDeprecatedPackages_CanBeConfiguredViaCommandLine()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Set the show deprecated packages feature flag to true
        var setResult = command.Parse($"config set {KnownFeatures.FeaturePrefix}.{KnownFeatures.ShowDeprecatedPackages} true");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        // Create new service provider to pick up the configuration change
        var newServices = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var newProvider = newServices.BuildServiceProvider();

        // Verify the feature flag is enabled
        var featureFlags = newProvider.GetRequiredService<IFeatures>();
        Assert.True(featureFlags.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false));
    }

    [Fact]
    public void ShowDeprecatedPackages_DefaultsToFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        // Verify the feature flag defaults to false
        var featureFlags = provider.GetRequiredService<IFeatures>();
        Assert.False(featureFlags.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false));
    }

    [Fact]
    public async Task ConfigSetCommand_WithColonNotation_CreatesNestedObject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("config set features:polyglotSupportEnabled true");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);

        // Colon notation should be normalized to nested JSON, not stored as a flat key
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);

        // Should be stored as nested object, not as flat "features:polyglotSupportEnabled" key
        Assert.False(settings.ContainsKey("features:polyglotSupportEnabled"));
        Assert.True(settings["features"] is JsonObject);
        var featuresObject = settings["features"]!.AsObject();
        Assert.Equal("true", featuresObject["polyglotSupportEnabled"]?.ToString());
    }

    [Fact]
    public async Task ConfigSetCommand_ColonThenDot_NoDuplicateKeys()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Set with colon notation first
        var result1 = command.Parse("config set features:polyglotSupportEnabled true");
        var exitCode1 = await result1.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode1);

        // Then set with dot notation
        var result2 = command.Parse("config set features.polyglotSupportEnabled false");
        var exitCode2 = await result2.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode2);

        // Should have a single nested entry, no flat colon key
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);

        Assert.False(settings.ContainsKey("features:polyglotSupportEnabled"));
        Assert.True(settings["features"] is JsonObject);
        var featuresObject = settings["features"]!.AsObject();
        Assert.Equal("false", featuresObject["polyglotSupportEnabled"]?.ToString());
    }

    [Fact]
    public async Task ConfigSetCommand_DotThenColon_NoDuplicateKeys()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Set with dot notation first
        var result1 = command.Parse("config set features.polyglotSupportEnabled true");
        var exitCode1 = await result1.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode1);

        // Then set with colon notation
        var result2 = command.Parse("config set features:polyglotSupportEnabled false");
        var exitCode2 = await result2.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode2);

        // Should have a single nested entry, no flat colon key
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);

        Assert.False(settings.ContainsKey("features:polyglotSupportEnabled"));
        Assert.True(settings["features"] is JsonObject);
        var featuresObject = settings["features"]!.AsObject();
        Assert.Equal("false", featuresObject["polyglotSupportEnabled"]?.ToString());
    }

    [Fact]
    public async Task ConfigDeleteCommand_WithColonNotation_DeletesNestedValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        // Set with dot notation
        var setResult = command.Parse("config set features.polyglotSupportEnabled true");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, setExitCode);

        // Delete with colon notation
        var deleteResult = command.Parse("config delete features:polyglotSupportEnabled");
        var deleteExitCode = await deleteResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, deleteExitCode);

        // Verify the entire features structure is cleaned up
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);
        Assert.False(settings.ContainsKey("features"));
    }

    [Fact]
    public async Task ConfigSetCommand_WithCorruptedFile_RecoversDuringLoad()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Manually create a corrupted settings file with duplicate keys
        // (flat colon key + nested object for the same path)
        var settingsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(settingsDir);
        var settingsPath = Path.Combine(settingsDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "features": {
                "polyglotSupportEnabled": "false"
              },
              "features:polyglotSupportEnabled": "true"
            }
            """);

        // Loading configuration should succeed after normalizing the corrupted file
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        // Verify the file was normalized - flat key should be gone, existing nested value preserved
        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);
        Assert.False(settings.ContainsKey("features:polyglotSupportEnabled"));
        Assert.True(settings["features"] is JsonObject);
        var featuresObject = settings["features"]!.AsObject();
        Assert.Equal("false", featuresObject["polyglotSupportEnabled"]?.ToString());
    }

    [Fact]
    public async Task ConfigSetCommand_WithCorruptedFile_PreservesExistingNestedValueOverFlatKey()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a settings file where both a nested value and a flat colon key exist
        // for the same path but with different values.
        var settingsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(settingsDir);
        var settingsPath = Path.Combine(settingsDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "features": {
                "polyglotSupportEnabled": "nested-value"
              },
              "features:polyglotSupportEnabled": "flat-value"
            }
            """);

        // Loading configuration triggers normalization
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        // The existing nested value should be preserved; the flat key value should be dropped
        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(settings);
        Assert.False(settings.ContainsKey("features:polyglotSupportEnabled"));
        var featuresObject = settings["features"]!.AsObject();
        Assert.Equal("nested-value", featuresObject["polyglotSupportEnabled"]?.ToString());
    }
}

public class TestConfigurationService : IConfigurationService
{
    public Task SetConfigurationAsync(string key, string value, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task<Dictionary<string, string>> GetAllConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>
        {
            { "testkey", "testvalue" },
            { "", "" },
        });
    }

    public Task<Dictionary<string, string>> GetLocalConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>
        {
            { "testkey", "testvalue" },
        });
    }

    public Task<Dictionary<string, string>> GetGlobalConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>());
    }

    public Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(key);
    }

    public string GetSettingsFilePath(bool isGlobal)
    {
        return string.Empty;
    }
}
