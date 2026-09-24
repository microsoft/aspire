import type { ResourceJson } from '../data/appHostCliContracts';
import { ResourceState, ResourceType } from '../editor/resourceConstants';
import type { EditorAssistanceResource, EditorAssistanceResourceState } from './editorAssistanceToolContracts';

const maxModelSafeResourceSourceLength = 256;
const maxModelSafeResourceMetadataLength = 128;

export function createBoundedResource(resource: ResourceJson): EditorAssistanceResource {
    return {
        resourceType: getModelSafeResourceMetadata(resource.resourceType) ?? 'unknown',
        state: getModelSafeResourceState(resource.state),
        healthStatus: getModelSafeResourceMetadata(resource.healthStatus),
        exitCode: resource.exitCode,
        source: getModelSafeResourceSource(resource),
    };
}

// Model-facing results need a smaller privacy boundary than the tree view. Rebuild source values
// from properties tied to known resource kinds so a custom resource cannot place arbitrary text in
// the canonical source field and have it copied into a tool result.
function getModelSafeResourceSource(resource: ResourceJson): string | null {
    let source: string | null | undefined;
    let useFileName = false;
    switch (resource.resourceType) {
        case ResourceType.Project:
            source = resource.properties?.['project.path'];
            useFileName = true;
            break;
        case ResourceType.Executable:
            source = resource.properties?.['executable.path'];
            useFileName = true;
            break;
        case ResourceType.Container:
            source = resource.properties?.['container.image'];
            break;
        default:
            return null;
    }

    if (typeof source !== 'string') {
        return null;
    }

    // Validate before trimming images so leading/trailing controls and U+FEFF are rejected,
    // rather than silently removed by trim(). Ordinary surrounding spaces are still allowed.
    const safeSource = getModelSafeResourceText(useFileName ? getPortableFileName(source) : source);
    if (safeSource === null) {
        return null;
    }

    const boundedSource = useFileName ? safeSource : safeSource.trim();
    return boundedSource.length > 0 && [...boundedSource].length <= maxModelSafeResourceSourceLength
        ? boundedSource
        : null;
}

function getPortableFileName(value: string): string | undefined {
    const separatorIndex = Math.max(value.lastIndexOf('/'), value.lastIndexOf('\\'));
    const fileName = value.slice(separatorIndex + 1);
    return fileName.trim().length > 0 ? fileName : undefined;
}

function getModelSafeResourceMetadata(value: string | null): string | null {
    return typeof value === 'string' && value.length <= maxModelSafeResourceMetadataLength
        ? getModelSafeResourceText(value)
        : null;
}

function getModelSafeResourceText(value: unknown): string | null {
    // Reject invisible controls (for example U+202E in "Api\u202e.csproj") and invalid or
    // non-display Unicode categories consistently across metadata and reconstructed sources.
    return typeof value === 'string' &&
        value.length > 0 &&
        !/[\p{Cc}\p{Cf}\p{Cs}\p{Co}\p{Cn}]/u.test(value)
        ? value
        : null;
}

function getModelSafeResourceState(state: string | null): EditorAssistanceResourceState {
    switch (state) {
        case ResourceState.Running:
        case ResourceState.Active:
        case ResourceState.Starting:
        case ResourceState.Building:
        case ResourceState.Stopping:
        case ResourceState.Stopped:
        case ResourceState.Waiting:
        case ResourceState.NotStarted:
        case ResourceState.Finished:
        case ResourceState.Exited:
        case ResourceState.FailedToStart:
        case ResourceState.RuntimeUnhealthy:
        case ResourceState.ValueMissing:
            return state;
        default:
            return 'unknown';
    }
}
