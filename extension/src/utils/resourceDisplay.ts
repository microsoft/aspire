import { ResourceState, ResourceType, CommandName, ParameterPropertyName } from '../editor/resourceConstants';
import { ResourceJson, ResourceCommandInputType, ResourceCommandJson } from '../views/AppHostDataRepository';
import { parameterValueMissing } from '../loc/strings';

// Sort commands by sort order, then name.
export function compareResourceCommands(
    [nameA, a]: [string, ResourceCommandJson],
    [nameB, b]: [string, ResourceCommandJson]): number {
    const orderA = a.sortOrder ?? 0;
    const orderB = b.sortOrder ?? 0;
    return orderA !== orderB ? orderA - orderB : nameA.localeCompare(nameB);
}

// Trim long parameter values so a single resource row stays readable in the tree and CodeLens.
const maxParameterValueDisplayLength = 80;
// Fixed 8-bullet mask, matching the dashboard's GridValue masking (GetMaskingText(length: 8)).
const maskedParameterValue = '●●●●●●●●';

// Humanize the runtime state for display.
export function getResourceStateDescription(state: string): string {
    return state === ResourceState.ValueMissing ? parameterValueMissing : state;
}

export function getParameterValueDescription(resource: ResourceJson): string | undefined {
    if (resource.resourceType !== ResourceType.Parameter || resource.state === ResourceState.ValueMissing) {
        return undefined;
    }

    // The backchannel redacts secret values to null. Check for the secret before the null/empty
    // guard below so the mask isn't lost.
    if (Object.prototype.hasOwnProperty.call(resource.properties ?? {}, ParameterPropertyName.Value) && isSecretParameter(resource)) {
        return maskedParameterValue;
    }

    const value = resource.properties?.[ParameterPropertyName.Value];
    if (typeof value !== 'string' || value.length === 0) {
        return undefined;
    }

    return truncateParameterValue(value);
}

function isSecretParameter(resource: ResourceJson): boolean {
    const setParameterCommand = resource.commands?.[CommandName.SetParameter];
    return setParameterCommand?.argumentInputs?.some(input =>
        input.name === ParameterPropertyName.Value &&
        input.inputType === ResourceCommandInputType.SecretText) ?? false;
}

function truncateParameterValue(value: string): string {
    if (value.length <= maxParameterValueDisplayLength) {
        return value;
    }

    return `${value.slice(0, maxParameterValueDisplayLength - 1)}…`;
}
