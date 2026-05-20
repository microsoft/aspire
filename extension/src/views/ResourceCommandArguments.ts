import * as vscode from 'vscode';
import {
    fieldRequired,
    noLabel,
    resourceCommandArgumentsTitle,
    resourceCommandArgumentInputTitle,
    resourceCommandContinue,
    resourceCommandCustomChoice,
    resourceCommandCustomChoiceDescription,
    resourceCommandDynamicInputsUnsupported,
    resourceCommandInvalidNumber,
    resourceCommandMaxLength,
    resourceCommandDontShowAgain,
    resourceCommandSecretWarning,
    yesLabel,
} from '../loc/strings';
import { ResourceCommandArgumentInputJson, ResourceCommandInputType, ResourceCommandJson } from './AppHostDataRepository';

export interface ResourceCommandArgumentValue {
    input: ResourceCommandArgumentInputJson;
    value: string;
}

export interface CollectedResourceCommandArguments {
    args: string[];
    containsSecret: boolean;
}

export type ResourceCommandArgumentLoader = (values: readonly ResourceCommandArgumentValue[]) => Promise<ResourceCommandArgumentInputJson[] | undefined>;

interface ResourceCommandChoiceItem extends vscode.QuickPickItem {
    value: string;
}

interface SecretWarningItem extends vscode.QuickPickItem {
    suppressFutureWarnings: boolean;
}

export const resourceCommandSecretWarningSuppressedKey = 'resourceCommandArguments.secretWarningSuppressed';

export interface ResourceCommandArgumentOptions {
    // Callers pass ExtensionContext.globalState here. The suppression is one extension-wide
    // preference for the current VS Code profile on this machine, not per workspace, AppHost,
    // project, resource, or command.
    secretWarningState?: vscode.Memento;
    loadDynamicArguments?: ResourceCommandArgumentLoader;
}

// Resource command number inputs are forwarded to hosting, which validates with
// double.TryParse(NumberStyles.Float, InvariantCulture). Accept examples like "1",
// "-1.5", ".5", and "1e3"; reject locale-specific values like "1,5".
const numberPattern = /^[+-]?(?:(?:\d+(?:\.\d*)?)|(?:\.\d+))(?:[eE][+-]?\d+)?$/;

export async function collectResourceCommandArguments(commandName: string, command: ResourceCommandJson | undefined, options?: ResourceCommandArgumentOptions): Promise<CollectedResourceCommandArguments | undefined> {
    let inputs = command?.argumentInputs ?? [];
    if (inputs.length === 0) {
        return { args: [], containsSecret: false };
    }

    if (hasDynamicInputs(inputs) && !options?.loadDynamicArguments) {
        await vscode.window.showWarningMessage(resourceCommandDynamicInputsUnsupported, { modal: true });
        return undefined;
    }

    const values: ResourceCommandArgumentValue[] = [];
    const commandTitle = resourceCommandArgumentsTitle(commandName);
    const loadedInputSignatures = new Set<string>();
    let secretWarningConfirmed = false;

    // Load before the first prompt so initially-disabled dependent inputs can become visible
    // once the AppHost applies defaults or AlwaysLoadOnStart callbacks.
    const initialLoadedInputs = await loadDynamicArgumentInputs(inputs, values, options?.loadDynamicArguments, loadedInputSignatures);
    if (!initialLoadedInputs) {
        return undefined;
    }

    inputs = initialLoadedInputs;
    if (inputs.length === 0) {
        return { args: [], containsSecret: false };
    }

    if (hasEnabledSecretInput(inputs)) {
        const confirmed = await confirmSecretArgumentWarning(options?.secretWarningState);
        if (!confirmed) {
            return undefined;
        }

        secretWarningConfirmed = true;
    }

    for (let i = 0; i < inputs.length; i++) {
        // Reload before each prompt because a previous answer can change later input metadata,
        // such as enabling an input, replacing choice options, or assigning a default value.
        const loadedInputs = await loadDynamicArgumentInputs(inputs, values, options?.loadDynamicArguments, loadedInputSignatures);
        if (!loadedInputs) {
            return undefined;
        }

        inputs = loadedInputs;
        const input = inputs[i];
        if (input.disabled) {
            continue;
        }

        if (!secretWarningConfirmed && input.inputType === ResourceCommandInputType.SecretText) {
            const confirmed = await confirmSecretArgumentWarning(options?.secretWarningState);
            if (!confirmed) {
                return undefined;
            }

            secretWarningConfirmed = true;
        }

        const enabledInputs = inputs.filter(input => !input.disabled);
        const value = await promptForArgumentValue(commandTitle, input, values.length + 1, enabledInputs.length);
        if (value === undefined) {
            return undefined;
        }

        values.push({ input, value });
    }

    return {
        args: buildResourceCommandCliArgs(values),
        containsSecret: values.some(({ input, value }) => input.inputType === ResourceCommandInputType.SecretText && shouldSubmitValue(input, value))
    };
}

export async function confirmSecretArgumentWarning(secretWarningState: vscode.Memento | undefined): Promise<boolean> {
    if (secretWarningState?.get<boolean>(resourceCommandSecretWarningSuppressedKey)) {
        return true;
    }

    const continueItem: SecretWarningItem = { label: resourceCommandContinue, suppressFutureWarnings: false };
    const dontShowAgainItem: SecretWarningItem = { label: resourceCommandDontShowAgain, suppressFutureWarnings: true };

    // Keep this warning in the QuickInput flow instead of a notification toast. Resource commands
    // already prompt for arguments here, and using a toast as a blocking pre-prompt can leave a
    // stale notification visible when focus immediately moves into the next input.
    const result = await vscode.window.showQuickPick(
        [continueItem, dontShowAgainItem],
        {
            title: resourceCommandSecretWarning,
            ignoreFocusOut: true,
        });

    if (result?.suppressFutureWarnings) {
        await secretWarningState?.update(resourceCommandSecretWarningSuppressedKey, true);
        return true;
    }

    return result !== undefined;
}

export function hasSecretResourceCommandArguments(command: ResourceCommandJson | undefined): boolean {
    return command?.argumentInputs?.some(input => !input.disabled && input.inputType === ResourceCommandInputType.SecretText) ?? false;
}

export function hasDynamicResourceCommandArguments(command: ResourceCommandJson | undefined): boolean {
    return command?.argumentInputs?.some(input => input.dynamicLoading) ?? false;
}

async function loadDynamicArgumentInputs(
    inputs: readonly ResourceCommandArgumentInputJson[],
    values: readonly ResourceCommandArgumentValue[],
    loader: ResourceCommandArgumentLoader | undefined,
    loadedInputSignatures: Set<string>): Promise<ResourceCommandArgumentInputJson[] | undefined> {
    if (!loader || !hasDynamicInputs(inputs)) {
        return [...inputs];
    }

    // Avoid repeatedly calling the AppHost for the same prompt state. Dynamic input names are
    // included because a load can replace the metadata shape as well as option/default values.
    const signature = createDynamicInputLoadSignature(inputs, values);
    if (loadedInputSignatures.has(signature)) {
        return [...inputs];
    }

    loadedInputSignatures.add(signature);
    const loadedInputs = await loader(values);
    if (!loadedInputs) {
        return undefined;
    }

    return preserveCollectedValues(loadedInputs, values);
}

function hasDynamicInputs(inputs: readonly ResourceCommandArgumentInputJson[]): boolean {
    return inputs.some(input => input.dynamicLoading);
}

function createDynamicInputLoadSignature(inputs: readonly ResourceCommandArgumentInputJson[], values: readonly ResourceCommandArgumentValue[]): string {
    const dynamicInputNames = inputs
        .filter(input => input.dynamicLoading)
        .map(input => input.name)
        .sort();
    const collectedValues = values
        .map(({ input, value }) => [input.name, value])
        .sort(([a], [b]) => a.localeCompare(b));

    return JSON.stringify({ dynamicInputNames, collectedValues });
}

function hasEnabledSecretInput(inputs: readonly ResourceCommandArgumentInputJson[]): boolean {
    return inputs.some(input => !input.disabled && input.inputType === ResourceCommandInputType.SecretText);
}

function preserveCollectedValues(inputs: readonly ResourceCommandArgumentInputJson[], values: readonly ResourceCommandArgumentValue[]): ResourceCommandArgumentInputJson[] {
    const valuesByName = new Map(values.map(({ input, value }) => [input.name, value]));
    // The AppHost returns fresh metadata, not the user's in-progress answers. Keep collected
    // values so later reloads don't erase earlier prompts while still honoring updated metadata.
    return inputs.map(input => valuesByName.has(input.name)
        ? { ...input, value: valuesByName.get(input.name)! }
        : input);
}

export function buildResourceCommandCliArgs(values: readonly ResourceCommandArgumentValue[]): string[] {
    const args: string[] = [];

    for (const { input, value } of values) {
        if (!shouldSubmitValue(input, value)) {
            continue;
        }

        const optionName = `--${input.name}`;
        if (input.inputType === ResourceCommandInputType.Boolean) {
            args.push(`${optionName}=${value === 'true' ? 'true' : 'false'}`);
        }
        else {
            // Use --name=value so that values starting with '-' or '--' are not parsed by
            // System.CommandLine as another option on the resource command.
            args.push(`${optionName}=${value}`);
        }
    }

    return args.length > 0 ? ['--', ...args] : [];
}

export function getResourceCommandArgumentValidationMessage(input: ResourceCommandArgumentInputJson, value: string): string | undefined {
    if (input.required && input.inputType !== ResourceCommandInputType.Boolean && value.trim().length === 0) {
        return fieldRequired;
    }

    if (input.maxLength !== null && input.maxLength !== undefined && value.length > input.maxLength) {
        return resourceCommandMaxLength(input.maxLength);
    }

    if (input.inputType === ResourceCommandInputType.Number && value.trim().length > 0 && !numberPattern.test(value.trim())) {
        return resourceCommandInvalidNumber;
    }

    return undefined;
}

async function promptForArgumentValue(title: string, input: ResourceCommandArgumentInputJson, step: number, totalSteps: number): Promise<string | undefined> {
    switch (input.inputType) {
        case ResourceCommandInputType.Choice:
            return promptForChoiceArgument(title, input, step, totalSteps);
        case ResourceCommandInputType.Boolean:
            return promptForBooleanArgument(title, input, step, totalSteps);
        case ResourceCommandInputType.Text:
        case ResourceCommandInputType.SecretText:
        case ResourceCommandInputType.Number:
            return promptForTextArgument(title, input, step, totalSteps);
    }
}

async function promptForTextArgument(title: string, input: ResourceCommandArgumentInputJson, step: number, totalSteps: number): Promise<string | undefined> {
    return new Promise<string | undefined>(resolve => {
        const inputBox = vscode.window.createInputBox();
        let settled = false;

        inputBox.title = getArgumentInputTitle(title, input);
        inputBox.step = step;
        inputBox.totalSteps = totalSteps;
        inputBox.value = input.value ?? '';
        inputBox.password = input.inputType === ResourceCommandInputType.SecretText;
        inputBox.prompt = getArgumentPrompt(input);
        inputBox.placeholder = input.placeholder ?? getArgumentLabel(input);
        inputBox.ignoreFocusOut = true;
        inputBox.validationMessage = getResourceCommandArgumentValidationMessage(input, inputBox.value);

        const finish = (value: string | undefined) => {
            if (!settled) {
                settled = true;
                inputBox.dispose();
                resolve(value);
            }
        };

        inputBox.onDidChangeValue(value => {
            inputBox.validationMessage = getResourceCommandArgumentValidationMessage(input, value);
        });
        inputBox.onDidAccept(() => {
            const validationMessage = getResourceCommandArgumentValidationMessage(input, inputBox.value);
            if (validationMessage) {
                inputBox.validationMessage = validationMessage;
                return;
            }

            finish(input.inputType === ResourceCommandInputType.Number ? inputBox.value.trim() : inputBox.value);
        });
        inputBox.onDidHide(() => finish(undefined));
        inputBox.show();
    });
}

async function promptForBooleanArgument(title: string, input: ResourceCommandArgumentInputJson, step: number, totalSteps: number): Promise<string | undefined> {
    const trueItem: ResourceCommandChoiceItem = { label: yesLabel, value: 'true' };
    const falseItem: ResourceCommandChoiceItem = { label: noLabel, value: 'false' };
    const items = [trueItem, falseItem];

    return new Promise<string | undefined>(resolve => {
        const quickPick = vscode.window.createQuickPick<ResourceCommandChoiceItem>();
        let settled = false;

        quickPick.title = getArgumentInputTitle(title, input);
        quickPick.step = step;
        quickPick.totalSteps = totalSteps;
        quickPick.placeholder = input.placeholder ?? getArgumentPrompt(input);
        quickPick.ignoreFocusOut = true;
        quickPick.items = items;
        quickPick.activeItems = [input.value?.toLowerCase() === 'true' ? trueItem : falseItem];

        const finish = (value: string | undefined) => {
            if (!settled) {
                settled = true;
                quickPick.dispose();
                resolve(value);
            }
        };

        quickPick.onDidAccept(() => finish((quickPick.selectedItems[0] ?? quickPick.activeItems[0])?.value));
        quickPick.onDidHide(() => finish(undefined));
        quickPick.show();
    });
}

async function promptForChoiceArgument(title: string, input: ResourceCommandArgumentInputJson, step: number, totalSteps: number): Promise<string | undefined> {
    const options = Object.entries(input.options ?? {});
    if (options.length === 0 && input.allowCustomChoice) {
        return promptForTextArgument(title, input, step, totalSteps);
    }

    return new Promise<string | undefined>(resolve => {
        const quickPick = vscode.window.createQuickPick<ResourceCommandChoiceItem>();
        let settled = false;

        quickPick.title = getArgumentInputTitle(title, input);
        quickPick.step = step;
        quickPick.totalSteps = totalSteps;
        quickPick.placeholder = input.placeholder ?? getArgumentPrompt(input);
        quickPick.ignoreFocusOut = true;
        quickPick.matchOnDescription = true;
        // Seed the QuickPick value so a custom default that is not among the predefined options
        // surfaces as the "Use custom value" item and is preselected.
        if (input.allowCustomChoice && input.value) {
            quickPick.value = input.value;
        }
        quickPick.items = createChoiceItems(input, quickPick.value);

        const activeItem = findChoiceItem(input, quickPick.items);
        if (activeItem) {
            quickPick.activeItems = [activeItem];
        }

        const finish = (value: string | undefined) => {
            if (!settled) {
                settled = true;
                quickPick.dispose();
                resolve(value);
            }
        };

        quickPick.onDidChangeValue(value => {
            if (input.allowCustomChoice) {
                quickPick.items = createChoiceItems(input, value);
                if (quickPick.items.length > 0 && quickPick.items[0].description === resourceCommandCustomChoiceDescription) {
                    quickPick.activeItems = [quickPick.items[0]];
                }
            }
        });
        quickPick.onDidAccept(() => {
            const selected = quickPick.selectedItems[0] ?? quickPick.activeItems[0];
            if (selected) {
                finish(selected.value);
                return;
            }

            if (input.allowCustomChoice) {
                finish(quickPick.value);
            }
        });
        quickPick.onDidHide(() => finish(undefined));
        quickPick.show();
    });
}

function createChoiceItems(input: ResourceCommandArgumentInputJson, customValue: string): ResourceCommandChoiceItem[] {
    const optionItems = Object.entries(input.options ?? {}).map(([value, label]) => ({
        label: label ?? value,
        description: label ? value : undefined,
        value,
    }));

    const trimmedCustomValue = customValue.trim();
    if (!input.allowCustomChoice || trimmedCustomValue.length === 0) {
        return optionItems;
    }

    const matchesExistingOption = optionItems.some(item =>
        item.value.localeCompare(trimmedCustomValue, undefined, { sensitivity: 'accent' }) === 0 ||
        item.label.localeCompare(trimmedCustomValue, undefined, { sensitivity: 'accent' }) === 0);

    if (matchesExistingOption) {
        return optionItems;
    }

    return [
        {
            label: resourceCommandCustomChoice(trimmedCustomValue),
            description: resourceCommandCustomChoiceDescription,
            value: trimmedCustomValue,
        },
        ...optionItems,
    ];
}

function findChoiceItem(input: ResourceCommandArgumentInputJson, items: readonly ResourceCommandChoiceItem[]): ResourceCommandChoiceItem | undefined {
    if (input.value) {
        return items.find(item => item.value === input.value);
    }

    return !input.placeholder && !input.allowCustomChoice ? items[0] : undefined;
}

function shouldSubmitValue(input: ResourceCommandArgumentInputJson, value: string): boolean {
    if (input.inputType === ResourceCommandInputType.Boolean) {
        return true;
    }

    if (value.length > 0) {
        return true;
    }

    // Empty number values cannot be parsed by the CLI, so fall back to the snapshot default.
    if (input.inputType === ResourceCommandInputType.Number) {
        return false;
    }

    // The user cleared a prefilled value, so submit the empty string to override the snapshot default.
    return (input.value ?? '').length > 0;
}

function getArgumentPrompt(input: ResourceCommandArgumentInputJson): string {
    const label = getArgumentLabel(input);
    return input.description ? resourceCommandArgumentInputTitle(label, input.description) : label;
}

function getArgumentInputTitle(commandTitle: string, input: ResourceCommandArgumentInputJson): string {
    return resourceCommandArgumentInputTitle(commandTitle, getArgumentLabel(input));
}

function getArgumentLabel(input: ResourceCommandArgumentInputJson): string {
    return input.label ?? input.name;
}
