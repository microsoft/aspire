// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class PipelineCommandInputManager
{
    private const string CustomChoiceValue = "__CUSTOM_CHOICE";
    private const string ParameterInputNamePrefix = "param-";
    private const string InputsEnvVarPrefix = "ASPIRE_INPUTS__";
    private const string ParametersEnvVarPrefix = "ASPIRE_PARAMETERS__";

    private readonly CliExecutionContext _executionContext;
    private readonly IInteractionService _interactionService;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly IAnsiConsole _ansiConsole;

    private Dictionary<string, string> _providedInputs = new(StringComparers.InteractionInputName);
    private Dictionary<string, string> _providedParams = new(StringComparers.InteractionInputName);

    public PipelineCommandInputManager(
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        ICliHostEnvironment hostEnvironment,
        IAnsiConsole ansiConsole)
    {
        _executionContext = executionContext;
        _interactionService = interactionService;
        _hostEnvironment = hostEnvironment;
        _ansiConsole = ansiConsole;
    }

    public void LoadProvidedValues(string[]? inputOptions, string[]? paramOptions)
    {
        _providedInputs = ParseOptions(inputOptions, StringComparers.InteractionInputName, InputsEnvVarPrefix, "--input");
        _providedParams = ParseOptions(paramOptions, StringComparers.InteractionInputName, ParametersEnvVarPrefix, "--param");
    }

    public bool IsInputProvided(string inputName)
    {
        if (inputName.StartsWith(ParameterInputNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var paramName = inputName[ParameterInputNamePrefix.Length..];
            return _providedParams.ContainsKey(paramName);
        }

        return _providedInputs.ContainsKey(inputName);
    }

    public async Task HandlePromptActivityAsync(
        PublishingActivity activity,
        IAppHostCliBackchannel backchannel,
        Func<string, PublishingActivityData, string> convertText,
        CancellationToken cancellationToken)
    {
        if (activity.Data.IsComplete)
        {
            // Prompt is already completed, nothing to do
            return;
        }

        // Check if we have input information
        if (activity.Data.Inputs is not { Count: > 0 } inputs)
        {
            throw new InvalidOperationException("Prompt provided without input data.");
        }

        // Check for validation errors. If there are errors then this isn't the first time the user has been prompted.
        var hasValidationErrors = inputs.Any(input => input.ValidationErrors is { Count: > 0 });

        // For multiple inputs, display the activity status text as a header.
        // Don't display if there are validation errors (header was already shown on first prompt),
        // if all inputs are resolved from --input/--param options, or if running non-interactively.
        var allInputsProvided = inputs.All(input => input.Name is not null && IsInputProvided(input.Name));
        if (!hasValidationErrors && inputs.Count > 1 && _hostEnvironment.SupportsInteractiveInput && !allInputsProvided)
        {
            var headerText = convertText(activity.Data.StatusText, activity.Data);
            _ansiConsole.MarkupLine($"[bold]{headerText}[/]");
        }

        // Handle multiple inputs
        var answers = new PublishingPromptInputAnswer[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];

            string? result;

            // Get prompt for input if there are no validation errors (first time we've asked)
            // or there are validation errors and this input has an error.
            if (!hasValidationErrors || input.ValidationErrors is { Count: > 0 })
            {
                // Build the prompt text based on number of inputs
                var promptText = BuildPromptText(input, inputs.Count, activity.Data.StatusText, activity.Data, convertText);

                result = await HandleSingleInputAsync(input, promptText, cancellationToken);
            }
            else
            {
                result = input.Value;
            }

            answers[i] = new PublishingPromptInputAnswer
            {
                Value = result
            };
        }

        // Send all results as an array
        await backchannel.CompletePromptResponseAsync(activity.Data.Id, answers, cancellationToken);
    }

    private async Task<string?> HandleSingleInputAsync(PublishingPromptInput input, string promptText, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<InputType>(input.InputType, ignoreCase: true, out var inputType))
        {
            // Fallback to text if unknown type
            inputType = InputType.Text;
        }

        // Check if the input value was provided via --input, --param, or environment variables
        if (TryResolveProvidedInput(input, inputType, out var providedValue))
        {
            // If the server returned validation errors for this input, fail since the
            // value was provided non-interactively and can't be corrected.
            if (input.ValidationErrors is { Count: > 0 } validationErrors)
            {
                var inputDisplayName = GetInputOptionDisplayName(input.Name);
                var validationMessage = string.Join("; ", validationErrors);
                throw new InputValidationException(
                    string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveInvalidValue, providedValue, inputDisplayName) + " " + validationMessage);
            }

            return providedValue;
        }

        // Display any validation errors.
        if (input.ValidationErrors is { Count: > 0 } errors)
        {
            foreach (var error in errors)
            {
                _interactionService.DisplayError(error);
            }
        }

        return inputType switch
        {
            InputType.Text => await _interactionService.PromptForStringAsync(
                promptText,
                binding: PromptBinding.CreateDefault(input.Value, input.Name ?? string.Empty, suppressNonInteractiveErrorDisplay: true),
                required: input.Required,
                cancellationToken: cancellationToken),

            InputType.SecretText => await _interactionService.PromptForStringAsync(
                promptText,
                binding: PromptBinding.CreateDefault(input.Value, input.Name ?? string.Empty, suppressNonInteractiveErrorDisplay: true),
                isSecret: true,
                required: input.Required,
                cancellationToken: cancellationToken),

            InputType.Choice => await HandleSelectInputAsync(input, promptText, cancellationToken),

            InputType.Boolean => (await _interactionService.PromptConfirmAsync(promptText, binding: PromptBinding.CreateDefault(ParseBooleanValue(input.Value), input.Name ?? string.Empty, suppressNonInteractiveErrorDisplay: true), cancellationToken: cancellationToken)).ToString().ToLowerInvariant(),

            InputType.Number => await HandleNumberInputAsync(input, promptText, cancellationToken),

            _ => await _interactionService.PromptForStringAsync(promptText, binding: PromptBinding.CreateDefault(input.Value, input.Name ?? string.Empty, suppressNonInteractiveErrorDisplay: true), required: input.Required, cancellationToken: cancellationToken)
        };
    }

    private async Task<string?> HandleSelectInputAsync(PublishingPromptInput input, string promptText, CancellationToken cancellationToken)
    {
        if (input.Options is null || input.Options.Count == 0)
        {
            return await _interactionService.PromptForStringAsync(promptText, binding: PromptBinding.CreateDefault(input.Value, input.Name ?? string.Empty, suppressNonInteractiveErrorDisplay: true), required: input.Required, cancellationToken: cancellationToken);
        }

        // If AllowCustomChoice is enabled then add an "Other" option to the list.
        // CLI doesn't support custom values directly in selection prompts. Instead an "Other" option is added.
        // If "Other" is selected then the user is prompted to enter a custom value as text.
        var options = input.Options.ToList();
        if (input.AllowCustomChoice)
        {
            options.Add(KeyValuePair.Create(CustomChoiceValue, InteractionServiceStrings.CustomChoiceLabel));
        }

        // For Choice inputs, we can't directly set a default in PromptForSelectionAsync,
        // but we can reorder the options to put the default first or use a different approach
        var (value, displayText) = await _interactionService.PromptForSelectionAsync(
            promptText,
            options,
            choice => choice.Value.EscapeMarkup(),
            cancellationToken: cancellationToken);

        if (value == CustomChoiceValue)
        {
            return await _interactionService.PromptForStringAsync(promptText, binding: PromptBinding.CreateDefault(input.Value, suppressNonInteractiveErrorDisplay: true), required: input.Required, cancellationToken: cancellationToken);
        }

        AnsiConsole.MarkupLine($"{promptText} {displayText.EscapeMarkup()}");

        return value;
    }

    private async Task<string?> HandleNumberInputAsync(PublishingPromptInput input, string promptText, CancellationToken cancellationToken)
    {
        static ValidationResult Validator(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !double.TryParse(value, out _))
            {
                return ValidationResult.Error("Please enter a valid number.");
            }

            return ValidationResult.Success();
        }

        return await _interactionService.PromptForStringAsync(
            promptText,
            binding: PromptBinding.CreateDefault(input.Value, input.Name ?? string.Empty, suppressNonInteractiveErrorDisplay: true),
            validator: Validator,
            required: input.Required,
            cancellationToken: cancellationToken);
    }

    private static string BuildPromptText(PublishingPromptInput input, int inputCount, string statusText, PublishingActivityData activityData, Func<string, PublishingActivityData, string> convertText)
    {
        if (inputCount > 1)
        {
            // Multi-input: just show the label with markdown conversion
            var labelText = convertText($"{input.Label}: ", activityData);
            return labelText;
        }

        // Single-input: show both StatusText and Label
        var header = statusText ?? string.Empty;
        var label = input.Label ?? string.Empty;

        // If StatusText equals Label (case-insensitive), show only the label once
        if (header.Equals(label, StringComparison.OrdinalIgnoreCase))
        {
            return $"[bold]{convertText(label, activityData)}[/]";
        }

        // Show StatusText as header (converted from markdown), then Label on new line
        var convertedHeader = convertText(header, activityData);
        var convertedLabel = convertText(label, activityData);
        return $"[bold]{convertedHeader}[/]\n{convertedLabel}: ";
    }

    private static bool ParseBooleanValue(string? value)
    {
        return bool.TryParse(value, out var result) && result;
    }

    private Dictionary<string, string> ParseOptions(string[]? options, StringComparer comparer, string envVarPrefix, string optionName)
    {
        var result = new Dictionary<string, string>(comparer);

        // Load environment variables with the given prefix (lowest priority)
        foreach (var (key, val) in _executionContext.GetEnvironmentVariables())
        {
            if (key.StartsWith(envVarPrefix, StringComparison.OrdinalIgnoreCase))
            {
                result[key[envVarPrefix.Length..]] = val;
            }
        }

        // CLI options override env vars
        if (options is { Length: > 0 })
        {
            foreach (var option in options)
            {
                var separatorIndex = option.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    throw new InputValidationException(
                        string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveInvalidValue, option, $"'{optionName}'"),
                        "name=value");
                }

                var name = option[..separatorIndex];
                var value = option[(separatorIndex + 1)..];
                result[name] = value;
            }
        }

        return result;
    }

    private bool TryResolveProvidedInput(PublishingPromptInput input, InputType inputType, out string? value)
    {
        value = null;

        if (input.Name is null)
        {
            return false;
        }

        string? providedValue;
        string optionDisplayName;

        if (input.Name.StartsWith(ParameterInputNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Parameter input: strip prefix and look up in --param values
            var paramName = input.Name[ParameterInputNamePrefix.Length..];
            if (!_providedParams.TryGetValue(paramName, out providedValue))
            {
                return false;
            }
            optionDisplayName = $"'--param {paramName}'";
        }
        else
        {
            // Regular input: look up in --input values (includes ASPIRE_INPUTS__ env vars)
            if (!_providedInputs.TryGetValue(input.Name, out providedValue))
            {
                return false;
            }
            optionDisplayName = $"'--input {input.Name}'";
        }

        switch (inputType)
        {
            case InputType.Choice:
                // Validate the value matches one of the allowed options
                if (input.Options is { Count: > 0 })
                {
                    var matchingOption = input.Options.FirstOrDefault(o => o.Key.Equals(providedValue, StringComparison.OrdinalIgnoreCase));
                    if (matchingOption.Key is not null)
                    {
                        value = matchingOption.Key;
                        return true;
                    }

                    if (input.AllowCustomChoice)
                    {
                        value = providedValue;
                        return true;
                    }

                    var availableValues = string.Join(", ", input.Options.Select(o => o.Key));
                    throw new InputValidationException(
                        string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveInvalidValue, providedValue, optionDisplayName),
                        availableValues);
                }

                value = providedValue;
                return true;

            case InputType.Boolean:
                if (!bool.TryParse(providedValue, out var boolValue))
                {
                    throw new InputValidationException(
                        string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveInvalidValue, providedValue, optionDisplayName),
                        "true, false");
                }

                value = boolValue.ToString().ToLowerInvariant();
                return true;

            case InputType.Number:
                if (!double.TryParse(providedValue, out _))
                {
                    throw new InputValidationException(
                        string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveInvalidValue, providedValue, optionDisplayName));
                }

                value = providedValue;
                return true;

            default:
                // Text, SecretText, and unknown types: return as-is
                value = providedValue;
                return true;
        }
    }

    private static string GetInputOptionDisplayName(string? inputName)
    {
        if (inputName is not null && inputName.StartsWith(ParameterInputNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var paramName = inputName[ParameterInputNamePrefix.Length..];
            return $"'--param {paramName}'";
        }

        return $"'--input {inputName}'";
    }

    internal sealed class InputValidationException(string message, string? availableValues = null) : Exception(message)
    {
        public string? AvailableValues { get; } = availableValues;
    }
}
