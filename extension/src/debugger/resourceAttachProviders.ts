import { isExtensionInstalled } from '../capabilities';
import { projectResourceAttachProvider } from './languages/dotnet';
import { goResourceAttachProvider } from './languages/go';
import {
    type ResourceAttachProvider,
    type ResourceDebugExtensionRequirement,
    type ResourceDebugResourceSnapshot,
} from './resourceDebugContracts';

export const extensionResourceAttachProviders: readonly ResourceAttachProvider[] = [
    projectResourceAttachProvider,
    goResourceAttachProvider,
];

export class ResourceAttachProviderRegistry {
    constructor(
        private readonly _knownProviders: readonly ResourceAttachProvider[],
        private readonly _isDebuggerExtensionInstalled?: (extensionId: string) => boolean,
    ) {
    }

    getRecognizedProviderForResource(resource: ResourceDebugResourceSnapshot): ResourceAttachProvider | undefined {
        return this._knownProviders.find(provider => provider.canRecognizeResource(resource));
    }

    getMissingDebuggerExtensions(provider: ResourceAttachProvider): readonly ResourceDebugExtensionRequirement[] {
        return provider.requiredDebuggerExtensions.filter(requirement =>
            !(this._isDebuggerExtensionInstalled?.(requirement.id) ?? isExtensionInstalled(requirement.id)));
    }
}
