import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { ResourceState, HealthStatus, StateStyle } from '../editor/resourceConstants';
import { compareResourceCommands, getParameterValueDescription, getResourceStateDescription } from '../utils/resourceDisplay';
import {
    tooltipType,
    tooltipState,
    tooltipHealth,
    tooltipEndpoints,
    resourceDescriptionHealth,
    resourceDescriptionExitCode,
} from '../loc/strings';
import { isLinkableUrl } from '../utils/urlSchemes';
import { ResourceCommandJson, ResourceJson } from '../data/AppHostDataRepository';

export const integratedBrowserOpenCommand = 'workbench.action.browser.open';
export const terminalEnabledPropertyName = 'terminal.enabled';
export const terminalReplicaIndexPropertyName = 'terminal.replicaIndex';

export function sortResources(resources: ResourceJson[]): ResourceJson[] {
    return [...resources].sort((a, b) => {
        const nameA = (a.displayName ?? a.name).toLowerCase();
        const nameB = (b.displayName ?? b.name).toLowerCase();
        return nameA.localeCompare(nameB);
    });
}

export function getVisibleResourceUrls(resource: ResourceJson) {
    return resource.urls?.filter(u => !u.isInternal && typeof u.url === 'string') ?? [];
}

export function getLinkableResourceUrls(resource: ResourceJson) {
    return getVisibleResourceUrls(resource).filter(u => isLinkableUrl(u.url));
}

export function hasNoResources(resources: readonly ResourceJson[] | null | undefined): boolean {
    return resources === undefined || resources === null || resources.length === 0;
}

export function getVisibleCommands(commands: Record<string, ResourceCommandJson>): [string, ResourceCommandJson][] {
    return Object.entries(commands)
        .filter(([, command]) => isCommandVisibleToUi(command) && (isEnabledCommand(command) || command.state === 'Disabled'))
        .sort(compareResourceCommands);
}

export function isEnabledCommand(command: ResourceCommandJson | null | undefined): boolean {
    return command !== null && command !== undefined
        && (command.state === undefined || command.state === null || command.state === 'Enabled');
}

export function isCommandVisibleToUi(command: ResourceCommandJson | null | undefined): boolean {
    const visibility = command?.visibility;
    if (visibility === undefined || visibility === null || visibility.trim().length === 0) {
        return true;
    }

    return visibility.split(',')
        .some(value => value.trim().toLowerCase() === 'ui');
}

/**
 * Maps a resource command to a Codicon. The CLI command JSON does not carry the dashboard's Fluent
 * icon name, so we can't reuse the per-command icons shown in the dashboard. Instead we map the
 * well-known lifecycle command names to distinct Codicons so they aren't all rendered with the same
 * glyph, and fall back to a generic "run" icon for custom commands. Command names can be emitted
 * either bare (`start`) or with a `resource-` prefix (`resource-start`) depending on the source, so
 * we match on the suffix.
 *
 * Some Codicons (e.g. `play`, `debug-stop`) carry intrinsic green/red theming that is visually noisy
 * in a dense tree, so we force a neutral foreground color for enabled commands and the standard
 * disabled foreground for disabled ones.
 */
export function getResourceCommandIcon(commandName: string, isEnabled: boolean): vscode.ThemeIcon {
    const color = new vscode.ThemeColor(isEnabled ? 'icon.foreground' : 'disabledForeground');
    const normalized = commandName.replace(/^resource-/, '');
    switch (normalized) {
        case 'start':
            return new vscode.ThemeIcon('play', color);
        case 'stop':
            return new vscode.ThemeIcon('debug-stop', color);
        case 'restart':
            return new vscode.ThemeIcon('debug-restart', color);
        case 'rebuild':
            return new vscode.ThemeIcon('tools', color);
        default:
            return new vscode.ThemeIcon('run', color);
    }
}

export function appHostIcon(path?: string): vscode.ThemeIcon {
    const icon = path?.endsWith('.csproj') ? 'server-process' : 'file-code';
    return new vscode.ThemeIcon(icon, new vscode.ThemeColor('aspire.brandPurple'));
}

export function getParentResourceName(resource: ResourceJson): string | null {
    return resource.properties?.['resource.parentName'] ?? null;
}

export function getResourceContextValue(resource: ResourceJson): string {
    const commands = resource.commands;
    const parts = ['resource'];
    if (hasEnabledCommand(commands, 'start') || hasEnabledCommand(commands, 'resource-start')) {
        parts.push('canStart');
    }
    if (hasEnabledCommand(commands, 'stop') || hasEnabledCommand(commands, 'resource-stop')) {
        parts.push('canStop');
    }
    if (hasEnabledCommand(commands, 'restart') || hasEnabledCommand(commands, 'resource-restart')) {
        parts.push('canRestart');
    }
    if (isTerminalEnabled(resource)) {
        parts.push('canOpenTerminal');
    }
    return parts.join(':');
}

export function hasEnabledCommand(commands: Record<string, ResourceCommandJson> | null | undefined, commandName: string): boolean {
    const command = commands?.[commandName];
    return isCommandVisibleToUi(command) && isEnabledCommand(command);
}

export function isTerminalEnabled(resource: ResourceJson): boolean {
    const value = resource.properties?.[terminalEnabledPropertyName];
    return value?.trim().toLowerCase() === 'true';
}

export function getTerminalReplicaIndex(resource: ResourceJson): string | undefined {
    const value = resource.properties?.[terminalReplicaIndexPropertyName];
    const trimmedValue = value?.trim();
    return trimmedValue && trimmedValue.length > 0 ? trimmedValue : undefined;
}

export function getResourceIcon(resource: ResourceJson): vscode.ThemeIcon {
    const state = resource.state;
    const health = resource.healthStatus;
    switch (state) {
        case ResourceState.ValueMissing:
            return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
        case ResourceState.Running:
        case ResourceState.Active:
            if (resource.stateStyle === StateStyle.Error) {
                return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
            }
            if (health === HealthStatus.Unhealthy) {
                return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
            }
            if (health === HealthStatus.Degraded || resource.stateStyle === StateStyle.Warning) {
                return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
            }
            return new vscode.ThemeIcon('pass', new vscode.ThemeColor('testing.iconPassed'));
        case ResourceState.Finished:
        case ResourceState.Exited:
        case ResourceState.Stopped:
            if (resource.stateStyle === StateStyle.Error || (resource.exitCode != null && resource.exitCode !== 0)) {
                return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
            }
            // Use a hollow circle (matches the `$(circle-outline)` codicon shown in the
            // "Stopped" code-lens label) instead of a green check, so a stopped/finished
            // resource is never visually confused with a Running one (both used to render
            // as a green check, just in slightly different greens).
            return new vscode.ThemeIcon('circle-outline', new vscode.ThemeColor('descriptionForeground'));
        case ResourceState.FailedToStart:
            if (resource.exitCode != null && resource.exitCode !== 0) {
                return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
            }
            return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
        case ResourceState.RuntimeUnhealthy:
            return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
        case ResourceState.Starting:
        case ResourceState.Stopping:
        case ResourceState.Building:
        case ResourceState.Waiting:
            return new vscode.ThemeIcon('loading~spin');
        case ResourceState.NotStarted:
            return new vscode.ThemeIcon('record', new vscode.ThemeColor('descriptionForeground'));
        default:
            if (state === null || state === undefined) {
                return new vscode.ThemeIcon('record', new vscode.ThemeColor('descriptionForeground'));
            }
            return new vscode.ThemeIcon('circle-filled', new vscode.ThemeColor('aspire.brandPurple'));
    }
}

export function resolveAppHostSourcePath(appHostPath: string, fileExists: (candidate: string) => boolean = fs.existsSync): string {
    if (!appHostPath.toLowerCase().endsWith('.csproj')) {
        return appHostPath;
    }

    const projectDirectory = path.dirname(appHostPath);
    // C# AppHosts are reported as the project file, but the tree action is meant to
    // take the user to the AppHost source code instead of opening project XML.
    const appHostCodePath = path.join(projectDirectory, 'AppHost.cs');
    if (fileExists(appHostCodePath)) {
        return appHostCodePath;
    }

    const fileBasedAppHostCodePath = path.join(projectDirectory, 'apphost.cs');
    if (fileExists(fileBasedAppHostCodePath)) {
        return fileBasedAppHostCodePath;
    }

    // Older/simple AppHosts may still use Program.cs, so prefer that before
    // falling back to the .csproj when no source file can be resolved.
    const programCodePath = path.join(projectDirectory, 'Program.cs');
    if (fileExists(programCodePath)) {
        return programCodePath;
    }

    return appHostPath;
}

export function buildResourceDescription(resource: ResourceJson): string {
    const parts: string[] = [resource.resourceType];
    const state = resource.state;
    if (state) {
        parts.push(getResourceStateDescription(state));
    }
    const parameterValue = getParameterValueDescription(resource);
    if (parameterValue) {
        parts.push(parameterValue);
    }
    const reports = resource.healthReports;
    const exitCode = resource.exitCode;
    if (reports && Object.keys(reports).length > 0) {
        const total = Object.keys(reports).length;
        const passed = Object.values(reports).filter(r => r.status === 'Healthy').length;
        parts.push(resourceDescriptionHealth(passed, total));
    }
    if (exitCode != null && exitCode !== 0) {
        parts.push(resourceDescriptionExitCode(exitCode));
    }
    return parts.join(' · ');
}

export function buildResourceTooltip(resource: ResourceJson): vscode.MarkdownString {
    const md = new vscode.MarkdownString();
    md.appendMarkdown(`**${resource.displayName ?? resource.name}**\n\n`);
    md.appendMarkdown(`${tooltipType(resource.resourceType)}\n\n`);
    if (resource.state) {
        md.appendMarkdown(`${tooltipState(getResourceStateDescription(resource.state))}\n\n`);
    }
    if (resource.healthStatus) {
        md.appendMarkdown(`${tooltipHealth(resource.healthStatus)}\n\n`);
        const reports = resource.healthReports;
        if (reports) {
            const entries = Object.entries(reports).sort(([a], [b]) => a.localeCompare(b));
            for (const [name, report] of entries) {
                let icon = '❓';
                if (report.status === HealthStatus.Healthy) {
                    icon = '✅';
                } else if (report.status === HealthStatus.Degraded) {
                    icon = '⚠️';
                } else if (report.status === HealthStatus.Unhealthy) {
                    icon = '❌';
                }
                md.appendMarkdown(`${icon} ${name}: ${report.status ?? 'Unknown'}${report.description ? ` - ${report.description}` : ''}\n\n`);
            }
        }
    }
    const urls = getLinkableResourceUrls(resource);
    if (urls.length > 0) {
        md.appendMarkdown(`**${tooltipEndpoints}**\n\n`);
        for (const url of urls) {
            md.appendMarkdown(`- [${url.displayName ?? url.url}](${url.url})\n`);
        }
    }
    md.isTrusted = { enabledCommands: [] };
    return md;
}
