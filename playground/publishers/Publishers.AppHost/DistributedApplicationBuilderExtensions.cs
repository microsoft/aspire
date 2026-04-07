// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001

using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;

internal static class IDistributedApplicationBuilderExtensions
{
    [AspireExportIgnore(Reason = "Publisher playground test resource; not part of the supported ATS surface.")]
    public static IResourceBuilder<IComputeEnvironmentResource>? AddPublishTestResource(this IDistributedApplicationBuilder builder, string name)
    {
        var resource = new PublishTestResource(name);
        return builder.AddResource(resource);
    }

    private sealed class PublishTestResource : Resource, IComputeEnvironmentResource
    {
        public PublishTestResource(string name) : base(name)
        {
            Annotations.Add(new PipelineStepAnnotation(context =>
            {
                var step = new PipelineStep
                {
                    Name = $"publish-{name}",
                    Action = PublishAsync
                };
                step.RequiredBy(WellKnownPipelineSteps.Publish);
                return step;
            }));
        }

        private async Task PublishAsync(PipelineStepContext context)
        {
            var reporter = context.ReportingStep;
            var interactionService = context.Services.GetRequiredService<IInteractionService>();

            // ALL PROMPTS FIRST - before any tasks are created

            // Test multiple inputs with validation
            var multiInputResult = await interactionService.PromptInputsAsync(
                "Application Configuration",
                "Configure additional application settings:",
                [
                    new InteractionInput
                    {
                        Name = "ApplicationName",
                        Label = "Application Name",
                        InputType = InputType.Text,
                        Required = true,
                        Placeholder = "my-app"
                    },
                    new InteractionInput
                    {
                        Name = "ApplicationVersion",
                        Label = "Application Version",
                        InputType = InputType.Text,
                        Required = false,
                        Placeholder = "1.0.0"
                    },
                    new InteractionInput
                    {
                        Name = "SSLCertificateType",
                        Label = "SSL Certificate Type",
                        InputType = InputType.Choice,
                        Required = true,
                        AllowCustomChoice = true,
                        Options =
                        [
                            new KeyValuePair<string, string>("self-signed", "Self-Signed Certificate"),
                            new KeyValuePair<string, string>("lets-encrypt", "Let's Encrypt Certificate")
                        ]
                    },
                    new InteractionInput
                    {
                        Name = "SSLCertificateTypeMoreDetails",
                        Label = "More certificate details",
                        InputType = InputType.Choice,
                        Required = true,
                        DynamicLoading = new InputLoadOptions
                        {
                            DependsOnInputs = ["SSLCertificateType"],
                            LoadCallback = async (c) =>
                            {
                                await Task.Delay(5000);
                                var dependsOnInput = c.AllInputs["SSLCertificateType"];
                                var options = new List<KeyValuePair<string, string>>();
                                for (var i = 0; i < 5; i++)
                                {
                                    options.Add(new KeyValuePair<string, string>($"{dependsOnInput.Value}-{i}", $"{dependsOnInput.Value}-{i}"));
                                }
                                c.Input.Options = options;
                            }
                        }
                    }
                ],
                new InputsDialogInteractionOptions
                {
                    ValidationCallback = async (validationContext) =>
                    {
                        var appNameInput = validationContext.Inputs.FirstOrDefault(i => i.Label == "Application Name");
                        if (appNameInput?.Value is not null && appNameInput.Value.Length < 3)
                        {
                            validationContext.AddValidationError(appNameInput, "Application name must be at least 3 characters long");
                        }

                        var versionInput = validationContext.Inputs.FirstOrDefault(i => i.Label == "Application Version");
                        if (versionInput?.Value is not null && !string.IsNullOrEmpty(versionInput.Value))
                        {
                            if (!System.Text.RegularExpressions.Regex.IsMatch(versionInput.Value, @"^\d+\.\d+\.\d+$"))
                            {
                                validationContext.AddValidationError(versionInput, "Version must be in format x.y.z (e.g., 1.0.0)");
                            }
                        }

                        await Task.CompletedTask;
                    }
                },
                cancellationToken: context.CancellationToken);

            var appName = multiInputResult.Canceled ? "default-app" : (multiInputResult.Data?.FirstOrDefault(i => i.Label == "Application Name")?.Value ?? "default-app");
            var appVersion = multiInputResult.Canceled ? "1.0.0" : (multiInputResult.Data?.FirstOrDefault(i => i.Label == "Application Version")?.Value ?? "1.0.0");
            var sslType = multiInputResult.Canceled ? "self-signed" : (multiInputResult.Data?.FirstOrDefault(i => i.Label == "SSL Certificate Type")?.Value ?? "self-signed");

            // Test Text input
            var envResult = await interactionService.PromptInputAsync(
                "Environment Configuration",
                "Please enter the target environment name:",
                new InteractionInput
                {
                    Name = "EnvironmentName",
                    Label = "Environment Name",
                    InputType = InputType.Text,
                    Required = true,
                    Placeholder = "dev, staging, prod"
                },
                new()
                {
                    ValidationCallback = validationContext =>
                    {
                        validationContext.AddValidationError(validationContext.Inputs[0], "Wrong");
                        return Task.CompletedTask;
                    }
                },
                cancellationToken: context.CancellationToken);
            var environmentName = envResult.Canceled ? "dev" : (envResult.Data?.Value ?? "dev");

            // Test Password input
            var dbPasswordResult = await interactionService.PromptInputAsync(
                "Database Configuration",
                "Please enter a secure database password:",
                new InteractionInput
                {
                    Name = "DatabasePassword",
                    Label = "Database Password",
                    InputType = InputType.SecretText,
                    Required = true,
                    Placeholder = "Enter a strong password"
                },
                cancellationToken: context.CancellationToken);
            var dbPassword = dbPasswordResult.Canceled ? "defaultPassword" : (dbPasswordResult.Data?.Value ?? "defaultPassword");

            // Test Select input
            var regionResult = await interactionService.PromptInputAsync(
                "Region Configuration",
                "Select the target deployment region:",
                new InteractionInput
                {
                    Name = "Region",
                    Label = "Region",
                    InputType = InputType.Choice,
                    Required = true,
                    Options =
                    [
                        new KeyValuePair<string, string>("us-west-2", "US West (Oregon)"),
                        new KeyValuePair<string, string>("us-east-1", "US East (N. Virginia)"),
                        new KeyValuePair<string, string>("eu-central-1", "Europe (Frankfurt)"),
                        new KeyValuePair<string, string>("ap-southeast-1", "Asia Pacific (Singapore)")
                    ]
                },
                cancellationToken: context.CancellationToken);
            var region = regionResult.Canceled ? "us-west-2" : (regionResult.Data?.Value ?? "us-west-2");

            // Test Checkbox input
            var enableLoggingResult = await interactionService.PromptInputAsync(
                "Logging Configuration",
                "Configure application logging settings:",
                new InteractionInput
                {
                    Name = "EnableVerboseLogging",
                    Label = "Enable Verbose Logging",
                    InputType = InputType.Boolean,
                    Required = false
                },
                cancellationToken: context.CancellationToken);
            var enableLogging = enableLoggingResult.Canceled ? false : bool.TryParse(enableLoggingResult.Data?.Value, out var logVal) && logVal;

            // Test Number input
            var instanceCountResult = await interactionService.PromptInputAsync(
                "Scaling Configuration",
                "Specify the number of application instances to deploy:",
                new InteractionInput
                {
                    Name = "InstanceCount",
                    Label = "Instance Count",
                    InputType = InputType.Number,
                    Required = true,
                    Placeholder = "1-10"
                },
                cancellationToken: context.CancellationToken);
            var instanceCount = instanceCountResult.Canceled ? 2 : (int.TryParse(instanceCountResult.Data?.Value, out var count) ? Math.Max(1, Math.Min(10, count)) : 2);

            // Test deployment strategy with Select input
            var deployModeResult = await interactionService.PromptInputAsync(
                "Deployment Strategy",
                "Choose your deployment strategy:",
                new InteractionInput
                {
                    Name = "Strategy",
                    Label = "Strategy",
                    InputType = InputType.Choice,
                    Required = true,
                    Options =
                    [
                        new KeyValuePair<string, string>("rolling", "Rolling Deployment (Zero Downtime)"),
                        new KeyValuePair<string, string>("blue-green", "Blue-Green Deployment (Full Replacement)"),
                        new KeyValuePair<string, string>("canary", "Canary Deployment (Gradual Rollout)")
                    ]
                },
                cancellationToken: context.CancellationToken);
            var deployMode = deployModeResult.Canceled ? "rolling" : (deployModeResult.Data?.Value ?? "rolling");
        }
    }
}
