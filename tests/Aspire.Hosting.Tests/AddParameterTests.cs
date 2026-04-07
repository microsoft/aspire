// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Publishing;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AddParameterTests
{
    [Fact]
    public void ParametersAreVisibleByDefault()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Configuration["Parameters:pass"] = "pass1";

        appBuilder.AddParameter("pass", secret: true);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());
        var annotation = parameterResource.Annotations.OfType<ResourceSnapshotAnnotation>().SingleOrDefault();

        Assert.NotNull(annotation);

        var state = annotation.InitialSnapshot;

        Assert.False(state.IsHidden);
        Assert.Collection(state.Properties,
            prop =>
            {
                Assert.Equal(CustomResourceKnownProperties.Source, prop.Name);
                Assert.Equal("Parameters:pass", prop.Value);
            });
    }

    [Fact]
    public void ParametersWithConfigurationValueDoNotGetDefaultValue()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:pass"] = "ValueFromConfiguration"
        });
        var parameter = appBuilder.AddParameter("pass");
        parameter.Resource.Default = new TestParameterDefault("DefaultValue");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal("ValueFromConfiguration", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Theory]
    // We test all the combinations of {direct param, callback param} x {config value, no config value}
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParametersWithDefaultValueStringOverloadUsedRegardlessOfConfigurationValue(bool useCallback, bool hasConfig)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        if (hasConfig)
        {
            appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Parameters:pass"] = "ValueFromConfiguration"
            });
        }

        if (useCallback)
        {
            appBuilder.AddParameter("pass", () => "DefaultValue");
        }
        else
        {
            appBuilder.AddParameter("pass", "DefaultValue");
        }

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Make sure the code value is used, ignoring any config value
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "pass");
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"DefaultValue", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        // The manifest should not include anything about the default value
        var paramManifest = await ManifestUtils.GetManifest(appModel.Resources.OfType<ParameterResource>().Single(r => r.Name == "pass")).DefaultTimeout();
        var expectedManifest = $$"""
            {
              "type": "parameter.v0",
              "value": "{pass.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string"
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, paramManifest.ToString());
    }

    [Theory]
    // We test all the combinations of {direct param, callback param} x {config value, no config value}
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParametersWithDefaultValueGetPublishedIfPublishFlagIsPassed(bool useCallback, bool hasConfig)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        if (hasConfig)
        {
            appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Parameters:pass"] = "ValueFromConfiguration"
            });
        }

        if (useCallback)
        {
            appBuilder.AddParameter("pass", () => "DefaultValue", publishValueAsDefault: true);
        }
        else
        {
            appBuilder.AddParameter("pass", "DefaultValue", publishValueAsDefault: true);
        }

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Make sure the code value is used, ignoring any config value
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "pass");
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"DefaultValue", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        // The manifest should include the default value, since we passed publishValueAsDefault: true
        var paramManifest = await ManifestUtils.GetManifest(appModel.Resources.OfType<ParameterResource>().Single(r => r.Name == "pass")).DefaultTimeout();
        var expectedManifest = $$"""
            {
              "type": "parameter.v0",
              "value": "{pass.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string",
                  "default": {
                    "value": "DefaultValue"
                  }
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, paramManifest.ToString());
    }

    [Fact]
    public void AddParameterWithBothPublishValueAsDefaultAndSecretFails()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        // publishValueAsDefault and secret are mutually exclusive. Test both overloads.
        var ex1 = Assert.Throws<ArgumentException>(() => appBuilder.AddParameter("pass", () => "SomeSecret", publishValueAsDefault: true, secret: true));
        Assert.Equal($"A parameter cannot be both secret and published as a default value. (Parameter 'secret')", ex1.Message);
        var ex2 = Assert.Throws<ArgumentException>(() => appBuilder.AddParameter("pass", "SomeSecret", publishValueAsDefault: true, secret: true));
        Assert.Equal($"A parameter cannot be both secret and published as a default value. (Parameter 'secret')", ex2.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParametersWithDefaultValueObjectOverloadUseConfigurationValueWhenPresent(bool hasConfig)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        if (hasConfig)
        {
            appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Parameters:pass"] = "ValueFromConfiguration"
            });
        }

        var genParam = new GenerateParameterDefault { MinLength = 10 };

        var parameter = appBuilder.AddParameter("pass", genParam);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Make sure the the generated default value is only used when there isn't a config value
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "pass");
        if (hasConfig)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            Assert.Equal("ValueFromConfiguration", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete
        }
        else
        {
#pragma warning disable CS0618 // Type or member is obsolete
            Assert.NotEqual("ValueFromConfiguration", parameterResource.Value);
            // We can't test the exact value since it's random, but we can test the length
            Assert.Equal(10, parameterResource.Value.Length);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        // The manifest should always include the fields for the generated default value
        var paramManifest = await ManifestUtils.GetManifest(appModel.Resources.OfType<ParameterResource>().Single(r => r.Name == "pass")).DefaultTimeout();
        var expectedManifest = $$"""
            {
              "type": "parameter.v0",
              "value": "{pass.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string",
                  "default": {
                    "generate": {
                      "minLength": 10
                    }
                  }
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, paramManifest.ToString());
    }

    [Fact]
    public void ParametersWithDefaultValueObjectOverloadOnlyGetWrappedWhenTheyShould()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        // Here it should get wrapped in UserSecretsParameterDefault, since we pass persist: true
        var parameter1 = appBuilder.AddParameter("val1", new GenerateParameterDefault(), persist: true);
        Assert.IsType<UserSecretsParameterDefault>(parameter1.Resource.Default);

        // Here it should not get wrapped, since we don't pass the persist flag
        var parameter2 = appBuilder.AddParameter("val2", new GenerateParameterDefault());
        Assert.IsType<GenerateParameterDefault>(parameter2.Resource.Default);
    }

    [Fact]
    public async Task ParametersCanGetValueFromNonDefaultConfigurationKeys()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:val"] = "ValueFromConfigurationParams",
            ["Auth:AccessToken"] = "MyAccessToken",
        });

        var parameter = appBuilder.AddParameterFromConfiguration("val", "Auth:AccessToken");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "val");
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"MyAccessToken", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        // The manifest is not affected by the custom configuration key
        var paramManifest = await ManifestUtils.GetManifest(appModel.Resources.OfType<ParameterResource>().Single(r => r.Name == "val")).DefaultTimeout();
        var expectedManifest = $$"""
                {
                  "type": "parameter.v0",
                  "value": "{val.inputs.value}",
                  "inputs": {
                    "value": {
                      "type": "string"
                    }
                  }
                }
                """;
        Assert.Equal(expectedManifest, paramManifest.ToString());
    }

    [Fact]
    public async Task AddConnectionStringParameterIsASecretParameterInTheManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddConnectionString("mycs");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var connectionStringResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        Assert.Equal("mycs", connectionStringResource.Name);
        var connectionStringManifest = await ManifestUtils.GetManifest(connectionStringResource).DefaultTimeout();

        var expectedManifest = $$"""
            {
              "type": "parameter.v0",
              "connectionString": "{mycs.value}",
              "value": "{mycs.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string",
                  "secret": true
                }
              }
            }
            """;

        var s = connectionStringManifest.ToString();

        Assert.Equal(expectedManifest, s);
    }

    private sealed class TestParameterDefault(string defaultValue) : ParameterDefault
    {
        public override string GetDefaultValue() => defaultValue;

        public override void WriteToManifest(ManifestPublishingContext context)
        {
            throw new NotImplementedException();
        }
    }

    [Fact]
    public void ParameterWithDescription_SetsDescriptionProperty()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("This is a test parameter");

        // Assert
        Assert.Equal("This is a test parameter", parameter.Resource.Description);
        Assert.False(parameter.Resource.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterWithMarkdownDescription_SetsDescriptionAndMarkupProperties()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("This is a **markdown** description", enableMarkdown: true);

        // Assert
        Assert.Equal("This is a **markdown** description", parameter.Resource.Description);
        Assert.True(parameter.Resource.EnableDescriptionMarkdown);
    }

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    [Fact]
    public void ParameterWithDescriptionAndCustomInput_AddsInputGeneratorAnnotation()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("Custom input parameter")
            .WithCustomInput(p => new InteractionInput
            {
                Name = "CustomInput",
                InputType = InputType.Number,
                Label = "Custom Label",
                Description = p.Description,
                EnableDescriptionMarkdown = p.EnableDescriptionMarkdown
            });

        // Assert
        Assert.Equal("Custom input parameter", parameter.Resource.Description);
        Assert.True(parameter.Resource.Annotations.OfType<InputGeneratorAnnotation>().Any());
    }

    [Fact]
    public void ParameterCreateInput_WithoutCustomGenerator_ReturnsDefaultInput()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("Test description");

        // Act
        var input = parameter.Resource.CreateInput();

        // Assert
        Assert.Equal(InputType.Text, input.InputType);
        Assert.Equal("test", input.Label);
        Assert.Equal("Test description", input.Description);
        Assert.Equal(string.Format(InteractionStrings.ParametersInputsParameterPlaceholder, "test"), input.Placeholder);
        Assert.False(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterCreateInput_ForSecretParameter_ReturnsSecretTextInput()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("secret", secret: true)
            .WithDescription("Secret description");

        // Act
        var input = parameter.Resource.CreateInput();

        // Assert
        Assert.Equal(InputType.SecretText, input.InputType);
        Assert.Equal("secret", input.Label);
        Assert.Equal("Secret description", input.Description);
        Assert.Equal(string.Format(InteractionStrings.ParametersInputsParameterPlaceholder, "secret"), input.Placeholder);
        Assert.False(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterCreateInput_WithCustomGenerator_UsesGenerator()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("Custom description")
            .WithCustomInput(p => new InteractionInput
            {
                Name = "TestParameter",
                InputType = InputType.Number,
                Label = "Custom Label",
                Description = "Custom: " + p.Description,
                EnableDescriptionMarkdown = true,
                Placeholder = "Enter number"
            });

        // Act
        var input = parameter.Resource.CreateInput();

        // Assert
        Assert.Equal(InputType.Number, input.InputType);
        Assert.Equal("Custom Label", input.Label);
        Assert.Equal("Custom: Custom description", input.Description);
        Assert.Equal("Enter number", input.Placeholder);
        Assert.True(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterCreateInput_WithMarkdownDescription_SetsMarkupFlag()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("**Bold** description", enableMarkdown: true);

        // Act
        var input = parameter.Resource.CreateInput();

        // Assert
        Assert.Equal("**Bold** description", input.Description);
        Assert.True(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterWithCustomInput_AddsInputGeneratorAnnotation()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var parameter = appBuilder.AddParameter("test")
            .WithCustomInput(p => new InteractionInput
            {
                Name = "TestParam",
                InputType = InputType.Number,
                Label = "Custom Label",
                Description = "Custom description",
                EnableDescriptionMarkdown = false
            });

        // Assert
        Assert.True(parameter.Resource.Annotations.OfType<InputGeneratorAnnotation>().Any());

        var input = parameter.Resource.CreateInput();
        Assert.Equal(InputType.Number, input.InputType);
        Assert.Equal("Custom Label", input.Label);
        Assert.Equal("Custom description", input.Description);
        Assert.False(input.EnableDescriptionMarkdown);
    }
#pragma warning restore ASPIREINTERACTION001

    [Fact]
    public async Task ParameterWithDashInName_CanBeResolvedWithUnderscoreInConfiguration()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        
        // Configuration using underscore instead of dash (as would come from environment variables or command line)
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:storage_account_name"] = "test-storage-account"
        });

        // Act - parameter defined with dash
        appBuilder.AddParameter("storage-account-name");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        // Assert - should resolve to the value set with underscore
        Assert.Equal("test-storage-account", await parameterResource.GetValueAsync(default));
    }

    [Fact]
    public async Task ParameterWithDashInName_PrefersDashConfigurationOverUnderscore()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        
        // Set both versions, dash version should take precedence
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:storage-account-name"] = "dash-value",
            ["Parameters:storage_account_name"] = "underscore-value"
        });

        // Act
        appBuilder.AddParameter("storage-account-name");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        // Assert - should prefer the exact match (with dash)
        Assert.Equal("dash-value", await parameterResource.GetValueAsync(default));
    }

    [Fact]
    public async Task ParameterWithoutDash_DoesNotFallbackToUnderscore()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        
        // Set only underscore version for a parameter without dash
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:storage_name"] = "underscore-value"
        });

        // Act
        appBuilder.AddParameter("storagename");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        // Assert - should not find the value because names don't match
        await Assert.ThrowsAsync<MissingParameterValueException>(async () =>
        {
            _ = await parameterResource.GetValueAsync(default);
        });
    }

    [Fact]
    public async Task ParameterWithCustomConfigurationKey_UsesFallback()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        
        // Set configuration with custom key that has underscore
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CustomSection:my_key"] = "custom-value"
        });

        // Act - use custom configuration key with dash
        appBuilder.AddParameterFromConfiguration("my-param", "CustomSection:my-key");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        // Assert - should find the value using the normalized key (dash -> underscore)
        Assert.Equal("custom-value", await parameterResource.GetValueAsync(default));
    }
}
