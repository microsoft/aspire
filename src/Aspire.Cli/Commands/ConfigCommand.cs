// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class ConfigCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private readonly IConfiguration _configuration;
    private readonly IInteractionService _interactionService;

    public ConfigCommand(IConfiguration configuration, IConfigurationService configurationService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("config", ConfigCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _configuration = configuration;
        _interactionService = interactionService;

        var getCommand = new GetCommand(configurationService, InteractionService, features, updateNotifier, executionContext, telemetry);
        var setCommand = new SetCommand(configurationService, InteractionService, features, updateNotifier, executionContext, telemetry);
        var listCommand = new ListCommand(configurationService, InteractionService, features, updateNotifier, executionContext, telemetry);
        var deleteCommand = new DeleteCommand(configurationService, InteractionService, features, updateNotifier, executionContext, telemetry);
        var infoCommand = new InfoCommand(configurationService, InteractionService, features, updateNotifier, executionContext, telemetry);

        Subcommands.Add(getCommand);
        Subcommands.Add(setCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(deleteCommand);
        Subcommands.Add(infoCommand);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (_configuration[KnownConfigNames.ExtensionPromptEnabled] is not "true")
        {
            new HelpAction().Invoke(parseResult);
            return ExitCodeConstants.InvalidCommand;
        }

        // Prompt for the action that the user wants to perform
        var subcommand = await _interactionService.PromptForSelectionAsync(
            ConfigCommandStrings.ExtensionActionPrompt,
            Subcommands.Cast<BaseConfigSubCommand>(),
            cmd =>
            {
                Debug.Assert(cmd.Description is not null);
                return cmd.Description.TrimEnd('.');
            },
            cancellationToken: cancellationToken);

        return await subcommand.InteractiveExecuteAsync(cancellationToken);
    }

    private sealed class GetCommand : BaseConfigSubCommand
    {
        private static readonly Argument<string> s_keyArgument = new("key")
        {
            Description = ConfigCommandStrings.GetCommand_KeyArgumentDescription
        };

        public GetCommand(IConfigurationService configurationService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("get", ConfigCommandStrings.GetCommand_Description, features, updateNotifier, configurationService, executionContext, interactionService, telemetry)
        {
            Arguments.Add(s_keyArgument);
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var key = parseResult.GetValue(s_keyArgument);
            if (key is null)
            {
                InteractionService.DisplayError(ErrorStrings.ConfigurationKeyRequired);
                return Task.FromResult(ExitCodeConstants.InvalidCommand);
            }

            return ExecuteAsync(key, cancellationToken);
        }

        public override async Task<int> InteractiveExecuteAsync(CancellationToken cancellationToken)
        {
            var key = await InteractionService.PromptForStringAsync(ConfigCommandStrings.GetCommand_PromptForKey, required: true, cancellationToken: cancellationToken);
            return await ExecuteAsync(key, cancellationToken);
        }

        private async Task<int> ExecuteAsync(string key, CancellationToken cancellationToken)
        {
            var value = await ConfigurationService.GetConfigurationAsync(key, cancellationToken);

            if (value is not null)
            {
                InteractionService.DisplayPlainText(value);
                return ExitCodeConstants.Success;
            }
            else
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfigurationKeyNotFound, key));
                return ExitCodeConstants.ConfigNotFound;
            }
        }
    }

    private sealed class SetCommand : BaseConfigSubCommand
    {
        private static readonly Argument<string> s_keyArgument = new("key")
        {
            Description = ConfigCommandStrings.SetCommand_KeyArgumentDescription
        };
        private static readonly Argument<string> s_valueArgument = new("value")
        {
            Description = ConfigCommandStrings.SetCommand_ValueArgumentDescription
        };
        private static readonly Option<bool> s_globalOption = new("--global", "-g")
        {
            Description = ConfigCommandStrings.SetCommand_GlobalArgumentDescription
        };

        public SetCommand(IConfigurationService configurationService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("set", ConfigCommandStrings.SetCommand_Description, features, updateNotifier, configurationService, executionContext, interactionService, telemetry)
        {
            Arguments.Add(s_keyArgument);
            Arguments.Add(s_valueArgument);
            Options.Add(s_globalOption);
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var key = parseResult.GetValue(s_keyArgument);
            var value = parseResult.GetValue(s_valueArgument);
            var isGlobal = parseResult.GetValue(s_globalOption);

            if (key is null)
            {
                InteractionService.DisplayError(ErrorStrings.ConfigurationKeyRequired);
                return Task.FromResult(ExitCodeConstants.InvalidCommand);
            }

            if (value is null)
            {
                InteractionService.DisplayError(ErrorStrings.ConfigurationValueRequired);
                return Task.FromResult(ExitCodeConstants.InvalidCommand);
            }

            return ExecuteAsync(key, value, isGlobal, cancellationToken);
        }

        public override async Task<int> InteractiveExecuteAsync(CancellationToken cancellationToken)
        {
            var key = await InteractionService.PromptForStringAsync(ConfigCommandStrings.SetCommand_PromptForKey, required: true, cancellationToken: cancellationToken);
            var value = await InteractionService.PromptForStringAsync(ConfigCommandStrings.SetCommand_PromptForValue, required: true, cancellationToken: cancellationToken);
            var isGlobal = await InteractionService.PromptForSelectionAsync(
                ConfigCommandStrings.SetCommand_PromptForGlobal,
                [false, true],
                g => g ? ConfigCommandStrings.SetCommand_PromptForGlobal_GlobalOption : ConfigCommandStrings.SetCommand_PromptForGlobal_LocalOption,
                cancellationToken: cancellationToken);

            return await ExecuteAsync(key, value, isGlobal, cancellationToken);
        }

        private async Task<int> ExecuteAsync(string key, string value, bool isGlobal, CancellationToken cancellationToken)
        {
            if (AppHostPathConfigurationPolicy.IsLegacyAppHostPathKey(key))
            {
                InteractionService.DisplayError(ErrorStrings.LegacyAppHostPathCannotBeSetWithConfigCommand);
                return ExitCodeConstants.InvalidCommand;
            }

            if (isGlobal && !AppHostPathConfigurationPolicy.IsGloballySettableKey(key))
            {
                InteractionService.DisplayError(ErrorStrings.GlobalAppHostPathCannotBeSetWithConfigCommand);
                return ExitCodeConstants.InvalidCommand;
            }

            try
            {
                await ConfigurationService.SetConfigurationAsync(key, value, isGlobal, cancellationToken);
                InteractionService.DisplaySuccess(isGlobal
                    ? string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationKeySetGlobally, key,
                        value)
                    : string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationKeySetLocally, key,
                        value));

                return ExitCodeConstants.Success;
            }
            catch (Exception ex)
            {
                var errorMessage = string.Format(CultureInfo.CurrentCulture, ErrorStrings.ErrorSettingConfiguration, ex.Message);
                Telemetry.RecordError(errorMessage, ex);
                InteractionService.DisplayError(errorMessage);
                return ExitCodeConstants.InvalidCommand;
            }
        }
    }

    private sealed class ListCommand : BaseConfigSubCommand
    {
        private static readonly Option<bool> s_allOption = new("--all")
        {
            Description = ConfigCommandStrings.ListCommand_AllOptionDescription
        };

        public ListCommand(IConfigurationService configurationService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("list", ConfigCommandStrings.ListCommand_Description, features, updateNotifier, configurationService, executionContext, interactionService, telemetry)
        {
            Options.Add(s_allOption);
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var showAll = parseResult.GetValue(s_allOption);
            return ExecuteAsync(showAll, cancellationToken);
        }

        public override Task<int> InteractiveExecuteAsync(CancellationToken cancellationToken)
        {
            return ExecuteAsync(showAll: false, cancellationToken);
        }

        private async Task<int> ExecuteAsync(bool showAll, CancellationToken cancellationToken)
        {
            if (InteractionService is ExtensionInteractionService extensionInteractionService)
            {
                var settingsFilePath = ConfigurationService.GetSettingsFilePath(isGlobal: false);
                if (Path.Exists(settingsFilePath))
                {
                    extensionInteractionService.OpenEditor(settingsFilePath);
                    return ExitCodeConstants.Success;
                }
            }

            var localConfig = await ConfigurationService.GetLocalConfigurationAsync(cancellationToken);
            var globalConfig = await ConfigurationService.GetGlobalConfigurationAsync(cancellationToken);

            var featurePrefix = $"{KnownFeatures.FeaturePrefix}.";

            // Check if we have any configuration at all
            if (localConfig.Count == 0 && globalConfig.Count == 0)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, ConfigCommandStrings.NoConfigurationValuesFound);

                if (!showAll)
                {
                    // Show hint about --all flag when there's no config and user didn't pass --all
                    InteractionService.DisplayMarkupLine($"  [dim]{ConfigCommandStrings.ListCommand_AllFeaturesHint.EscapeMarkup()}[/]");
                    return ExitCodeConstants.Success;
                }

                // showAll=true: fall through to show available features below
            }
            else
            {
                // Compute max column widths across both tables for consistent alignment
                var keyWidth = MaxWidth(ConfigCommandStrings.HeaderKey, localConfig.Keys, globalConfig.Keys);
                var valueWidth = MaxWidth(ConfigCommandStrings.HeaderValue, localConfig.Values, globalConfig.Values);

                // Display Local Configuration
                RenderConfigTable(
                    ConfigCommandStrings.LocalConfigurationHeader,
                    localConfig,
                    ConfigCommandStrings.NoLocalConfigurationFound,
                    keyWidth,
                    valueWidth);

                InteractionService.DisplayEmptyLine();

                // Display Global Configuration
                RenderConfigTable(
                    ConfigCommandStrings.GlobalConfigurationHeader,
                    globalConfig,
                    ConfigCommandStrings.NoGlobalConfigurationFound,
                    keyWidth,
                    valueWidth);
            }

            // Display Available Features
            var allConfiguredFeatures = localConfig.Concat(globalConfig)
                .Where(kvp => kvp.Key.StartsWith(featurePrefix, StringComparison.Ordinal))
                .Select(kvp => kvp.Key.Substring(featurePrefix.Length))
                .ToHashSet(StringComparer.Ordinal);

            var unconfiguredFeatures = KnownFeatures.GetAllFeatureMetadata()
                .Where(f => !allConfiguredFeatures.Contains(f.Name))
                .ToList();

            if (unconfiguredFeatures.Count > 0)
            {
                InteractionService.DisplayEmptyLine();
                InteractionService.DisplayMarkdown($"**{ConfigCommandStrings.AvailableFeaturesHeader}:**");

                if (showAll)
                {
                    foreach (var feature in unconfiguredFeatures)
                    {
                        var defaultText = feature.DefaultValue ? "true" : "false";
                        InteractionService.DisplayMarkupLine($"  [cyan]{feature.Name.EscapeMarkup()}[/] [dim](default: {defaultText})[/]");
                        InteractionService.DisplayMarkupLine($"    [dim]{feature.Description.EscapeMarkup()}[/]");
                    }
                    InteractionService.DisplayEmptyLine();
                    InteractionService.DisplayMarkupLine($"  [dim]{ConfigCommandStrings.SetFeatureHint.EscapeMarkup()}[/]");
                }
                else
                {
                    InteractionService.DisplayMarkupLine($"  [dim]{ConfigCommandStrings.ListCommand_AllFeaturesHint.EscapeMarkup()}[/]");
                }
            }

            return ExitCodeConstants.Success;

            static int MaxWidth(string header, IEnumerable<string> localValues, IEnumerable<string> globalValues)
            {
                const int minColumnWidth = 30;

                return localValues.Concat(globalValues)
                    .Select(s => s.Length)
                    .Append(header.Length)
                    .Append(minColumnWidth)
                    .Max();
            }
        }

        private void RenderConfigTable(string title, Dictionary<string, string> config, string emptyMessage, int keyWidth, int valueWidth)
        {
            var table = new Table();
            table.Title = new TableTitle($"[bold]{title.EscapeMarkup()}[/]");
            table.AddBoldColumn(ConfigCommandStrings.HeaderKey, width: keyWidth);
            table.AddBoldColumn(ConfigCommandStrings.HeaderValue, width: valueWidth);

            if (config.Count > 0)
            {
                foreach (var kvp in config.OrderBy(k => k.Key))
                {
                    table.AddRow(
                        $"[cyan]{kvp.Key.EscapeMarkup()}[/]",
                        $"[yellow]{kvp.Value.EscapeMarkup()}[/]");
                }
            }
            else
            {
                table.AddRow($"[dim]{emptyMessage.EscapeMarkup()}[/]", "");
            }

            InteractionService.DisplayRenderable(table);
        }
    }

    private sealed class DeleteCommand : BaseConfigSubCommand
    {
        private static readonly Argument<string> s_keyArgument = new("key")
        {
            Description = ConfigCommandStrings.DeleteCommand_KeyArgumentDescription
        };
        private static readonly Option<bool> s_globalOption = new("--global", "-g")
        {
            Description = ConfigCommandStrings.DeleteCommand_GlobalArgumentDescription
        };

        public DeleteCommand(IConfigurationService configurationService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("delete", ConfigCommandStrings.DeleteCommand_Description, features, updateNotifier, configurationService, executionContext, interactionService, telemetry)
        {
            Arguments.Add(s_keyArgument);
            Options.Add(s_globalOption);
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var key = parseResult.GetValue(s_keyArgument);
            var isGlobal = parseResult.GetValue(s_globalOption);

            if (key is null)
            {
                InteractionService.DisplayError(ErrorStrings.ConfigurationKeyRequired);
                return Task.FromResult(ExitCodeConstants.InvalidCommand);
            }

            return ExecuteAsync(key, isGlobal, cancellationToken);
        }

        public override async Task<int> InteractiveExecuteAsync(CancellationToken cancellationToken)
        {
            var key = await InteractionService.PromptForStringAsync(ConfigCommandStrings.DeleteCommand_PromptForKey, required: true, cancellationToken: cancellationToken);

            var value = await ConfigurationService.GetConfigurationAsync(key, cancellationToken);
            if (value is null)
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfigurationKeyNotFound, key));
                return ExitCodeConstants.ConfigNotFound;
            }

            var isGlobal = await InteractionService.PromptForSelectionAsync(
                ConfigCommandStrings.DeleteCommand_PromptForGlobal,
                [false, true],
                g => g ? TemplatingStrings.Yes : TemplatingStrings.No,
                cancellationToken: cancellationToken);

            return await ExecuteAsync(key, isGlobal, cancellationToken);
        }

        private async Task<int> ExecuteAsync(string key, bool isGlobal, CancellationToken cancellationToken)
        {
            try
            {
                var deleted = await ConfigurationService.DeleteConfigurationAsync(key, isGlobal, cancellationToken);

                if (deleted)
                {
                    if (isGlobal)
                    {
                        InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationKeyDeletedGlobally, key));
                    }
                    else
                    {
                        InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationKeyDeletedLocally, key));
                    }

                    return ExitCodeConstants.Success;
                }
                else
                {
                    InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfigurationKeyNotFound, key));
                    return ExitCodeConstants.InvalidCommand;
                }
            }
            catch (Exception ex)
            {
                var errorMessage = string.Format(CultureInfo.CurrentCulture, ErrorStrings.ErrorDeletingConfiguration, ex.Message);
                Telemetry.RecordError(errorMessage, ex);
                InteractionService.DisplayError(errorMessage);
                return ExitCodeConstants.InvalidCommand;
            }
        }
    }

    private sealed class InfoCommand : BaseConfigSubCommand
    {
        public InfoCommand(IConfigurationService configurationService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("info", ConfigCommandStrings.InfoCommand_Description, features, updateNotifier, configurationService, executionContext, interactionService, telemetry)
        {
            // Hide from help - this command is intended for tooling (VS Code extension) use only
            this.Hidden = true;
            
            var jsonOption = new Option<bool>("--json")
            {
                Description = ConfigCommandStrings.InfoCommand_JsonOptionDescription
            };
            Options.Add(jsonOption);
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var useJson = parseResult.GetValue<bool>("--json");
            return ExecuteAsync(useJson);
        }

        public override Task<int> InteractiveExecuteAsync(CancellationToken cancellationToken)
        {
            return ExecuteAsync(useJson: false);
        }

        private Task<int> ExecuteAsync(bool useJson)
        {
            var localPath = ConfigurationService.GetSettingsFilePath(isGlobal: false);
            var globalPath = ConfigurationService.GetSettingsFilePath(isGlobal: true);
            var availableFeatures = KnownFeatures.GetAllFeatureMetadata()
                .Select(m => new FeatureInfo(m.Name, m.Description, m.DefaultValue))
                .ToList();
            var localSchema = SettingsSchemaBuilder.BuildSchema(excludeLocalOnly: false);
            var globalSchema = SettingsSchemaBuilder.BuildSchema(excludeLocalOnly: true);

            if (useJson)
            {
                var configFileSchema = SettingsSchemaBuilder.BuildConfigFileSchema(excludeLocalOnly: false);
                var info = new ConfigInfo(localPath, globalPath, availableFeatures, localSchema, globalSchema, configFileSchema, KnownCapabilities.GetAdvertisedCapabilities());
                var json = System.Text.Json.JsonSerializer.Serialize(info, JsonSourceGenerationContext.Default.ConfigInfo);
                // Use DisplayRawText to avoid Spectre.Console word wrapping which breaks JSON strings
                if (InteractionService is ConsoleInteractionService consoleService)
                {
                    // Structured output always goes to stdout.
                    consoleService.DisplayRawText(json, ConsoleOutput.Standard);
                }
                else
                {
                    InteractionService.DisplayPlainText(json);
                }
            }
            else
            {
                InteractionService.DisplayMarkdown($"**{ConfigCommandStrings.InfoCommand_LocalSettingsPath}:**");
                InteractionService.DisplayPlainText($"  {localPath}");
                InteractionService.DisplayEmptyLine();
                InteractionService.DisplayMarkdown($"**{ConfigCommandStrings.InfoCommand_GlobalSettingsPath}:**");
                InteractionService.DisplayPlainText($"  {globalPath}");
                InteractionService.DisplayEmptyLine();
                InteractionService.DisplayMarkdown($"**{ConfigCommandStrings.InfoCommand_AvailableFeatures}:**");
                foreach (var feature in availableFeatures)
                {
                    InteractionService.DisplayMarkupLine($"  [cyan]{feature.Name.EscapeMarkup()}[/] - {feature.Description.EscapeMarkup()} [dim](default: {feature.DefaultValue})[/]");
                }
                InteractionService.DisplayEmptyLine();
                InteractionService.DisplayMarkdown($"**{ConfigCommandStrings.InfoCommand_SettingsProperties}:**");
                foreach (var property in localSchema.Properties)
                {
                    var requiredText = property.Required ? "[red]*[/]" : "";
                    InteractionService.DisplayMarkupLine($"  {requiredText}[cyan]{property.Name.EscapeMarkup()}[/] ([yellow]{property.Type.EscapeMarkup()}[/]) - {property.Description.EscapeMarkup()}");
                }
            }

            return Task.FromResult(ExitCodeConstants.Success);
        }
    }
}
