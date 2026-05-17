import * as vscode from 'vscode';
import {
    fieldRequired,
    noLabel,
    resourceCommandArgumentsTitle,
    resourceCommandContinue,
    resourceCommandCustomChoice,
    resourceCommandCustomChoiceDescription,
    resourceCommandInvalidNumber,
    resourceCommandMaxLength,
    resourceCommandSecretWarning,
    yesLabel,
} from '../loc/strings';
import { ResourceCommandArgumentInputJson, ResourceCommandJson } from './AppHostDataRepository';

export interface ResourceCommandArgumentValue {
    input: ResourceCommandArgumentInputJson;
    value: string;
}

interface ResourceCommandChoiceItem extends vscode.QuickPickItem {
    value: string;
}

const numberPattern = /^-?\d+(\.\d+)?$/;

export async function collectResourceCommandArguments(commandName: string, command: ResourceCommandJson | undefined): Promise<string[] | undefined> {
    const inputs = command?.argumentInputs?.filter(input => !input.disabled) ?? [];
    if (inputs.length === 0) {
        return [];
    }

    if (inputs.some(input => input.inputType === 'SecretText')) {
        const result = await vscode.window.showWarningMessage(resourceCommandSecretWarning, { modal: true }, resourceCommandContinue);
        if (result !== resourceCommandContinue) {
            return undefined;
        }
    }

    const values: ResourceCommandArgumentValue[] = [];
    const title = resourceCommandArgumentsTitle(commandName);

    for (let i = 0; i < inputs.length; i++) {
        const input = inputs[i];
        const value = await promptForArgumentValue(title, input, i + 1, inputs.length);
        if (value === undefined) {
            return undefined;
        }

        values.push({ input, value });
    }

    return buildResourceCommandCliArgs(values);
}

export function buildResourceCommandCliArgs(values: readonly ResourceCommandArgumentValue[]): string[] {
    const args: string[] = [];

    for (const { input, value } of values) {
        if (!shouldSubmitValue(input, value)) {
            continue;
        }

        const optionName = `--${input.name}`;
        if (input.inputType === 'Boolean') {
            args.push(`${optionName}=${value === 'true' ? 'true' : 'false'}`);
        }
        else {
            args.push(optionName, value);
        }
    }

    return args.length > 0 ? ['--', ...args] : [];
}

export function getResourceCommandArgumentValidationMessage(input: ResourceCommandArgumentInputJson, value: string): string | undefined {
    if (input.required && input.inputType !== 'Boolean' && value.trim().length === 0) {
        return fieldRequired;
    }

    if (input.maxLength !== null && input.maxLength !== undefined && value.length > input.maxLength) {
        return resourceCommandMaxLength(input.maxLength);
    }

    if (input.inputType === 'Number' && value.trim().length > 0 && !numberPattern.test(value.trim())) {
        return resourceCommandInvalidNumber;
    }

    return undefined;
}

async function promptForArgumentValue(title: string, input: ResourceCommandArgumentInputJson, step: number, totalSteps: number): Promise<string | undefined> {
    switch (input.inputType) {
        case 'Choice':
            return promptForChoiceArgument(title, input, step, totalSteps);
        case 'Boolean':
            return promptForBooleanArgument(title, input, step, totalSteps);
        case 'Text':
        case 'SecretText':
        case 'Number':
            return promptForTextArgument(title, input, step, totalSteps);
    }
}

async function promptForTextArgument(title: string, input: ResourceCommandArgumentInputJson, step: number, totalSteps: number): Promise<string | undefined> {
    return new Promise<string | undefined>(resolve => {
        const inputBox = vscode.window.createInputBox();
        let settled = false;

        inputBox.title = title;
        inputBox.step = step;
        inputBox.totalSteps = totalSteps;
        inputBox.value = input.value ?? '';
        inputBox.password = input.inputType === 'SecretText';
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

            finish(input.inputType === 'Number' ? inputBox.value.trim() : inputBox.value);
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

        quickPick.title = title;
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

        quickPick.title = title;
        quickPick.step = step;
        quickPick.totalSteps = totalSteps;
        quickPick.placeholder = input.placeholder ?? getArgumentPrompt(input);
        quickPick.ignoreFocusOut = true;
        quickPick.matchOnDescription = true;
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
    if (input.inputType === 'Boolean') {
        return true;
    }

    return value.length > 0;
}

function getArgumentPrompt(input: ResourceCommandArgumentInputJson): string {
    return input.description ?? getArgumentLabel(input);
}

function getArgumentLabel(input: ResourceCommandArgumentInputJson): string {
    return input.label ?? input.name;
}
