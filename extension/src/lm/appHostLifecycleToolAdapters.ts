import * as vscode from 'vscode';

import {
    appHostLifecycleStartConfirmationMessage,
    appHostLifecycleStartConfirmationMessageIsolated,
    appHostLifecycleStartConfirmationMessageIsolatedWithLaunchProfile,
    appHostLifecycleStartConfirmationMessageWithLaunchProfile,
    appHostLifecycleStartConfirmationTitle,
    appHostLifecycleStartInvocationMessage,
    appHostLifecycleInvalidLaunchProfile,
    appHostLifecycleStopConfirmationMessage,
    appHostLifecycleStopConfirmationTitle,
    appHostLifecycleStopInvocationMessage,
    appHostLifecycleUnspecifiedMode,
} from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    aspireAppHostStartToolName,
    aspireAppHostStopToolName,
    parseMode,
    type AppHostLifecycleToolRegistration,
    type AppHostLifecycleToolResult,
    type AppHostStartToolInput,
    type AppHostStopToolInput,
    type PreparableAppHostLifecycleTool,
} from './appHostLifecycleToolContracts';
import { AppHostLifecycleToolService } from './appHostLifecycleToolService';
import { escapeMarkdownForConfirmation } from './markdown';
import { isValidLaunchProfile } from '../utils/launchProfile';

export class AppHostStartLanguageModelTool implements vscode.LanguageModelTool<AppHostStartToolInput> {
    constructor(private readonly _service: AppHostLifecycleToolService) {
    }

    // Preparation resolves the requested selector against the AppHost registry so the
    // confirmation shows the exact target `invoke` will act on. It performs discovery but
    // no lifecycle work, which is what the API requires of a preparation step.
    async prepareInvocation(options: vscode.LanguageModelToolInvocationPrepareOptions<AppHostStartToolInput>, token: vscode.CancellationToken): Promise<vscode.PreparedToolInvocation> {
        const description = await this._service.describeStartTarget(options.input, token);
        const displayPath = escapeMarkdownForConfirmation(description.displayPath);
        const displayMode = describeRequestedMode(options.input?.mode);
        const displayLaunchProfile = describeLaunchProfile(options.input?.launchProfile);
        return {
            invocationMessage: appHostLifecycleStartInvocationMessage(displayPath),
            confirmationMessages: {
                title: appHostLifecycleStartConfirmationTitle,
                message: displayLaunchProfile === undefined
                    ? description.isolated
                        ? appHostLifecycleStartConfirmationMessageIsolated(displayPath, displayMode)
                        : appHostLifecycleStartConfirmationMessage(displayPath, displayMode)
                    : description.isolated
                        ? appHostLifecycleStartConfirmationMessageIsolatedWithLaunchProfile(displayPath, displayMode, displayLaunchProfile)
                        : appHostLifecycleStartConfirmationMessageWithLaunchProfile(displayPath, displayMode, displayLaunchProfile),
            },
        };
    }

    async invoke(options: vscode.LanguageModelToolInvocationOptions<AppHostStartToolInput>, token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._service.start(options.input, token));
    }
}

export class AppHostStopLanguageModelTool implements vscode.LanguageModelTool<AppHostStopToolInput> {
    constructor(private readonly _service: AppHostLifecycleToolService) {
    }

    async prepareInvocation(options: vscode.LanguageModelToolInvocationPrepareOptions<AppHostStopToolInput>, token: vscode.CancellationToken): Promise<vscode.PreparedToolInvocation> {
        const displayPath = escapeMarkdownForConfirmation(await this._service.describeTarget(options.input?.appHostPath, token));
        return {
            invocationMessage: appHostLifecycleStopInvocationMessage(displayPath),
            confirmationMessages: {
                title: appHostLifecycleStopConfirmationTitle,
                message: appHostLifecycleStopConfirmationMessage(displayPath),
            },
        };
    }

    async invoke(options: vscode.LanguageModelToolInvocationOptions<AppHostStopToolInput>, token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._service.stop(options.input, token));
    }
}

/**
 * Registers the AppHost lifecycle tools when the stable
 * {@link vscode.lm.registerTool} API exists.
 *
 * The API check keeps the extension loadable on VS Code builds that predate the
 * finalized language model tool API (`engines.vscode` allows older hosts). The
 * implementation is registered in Restricted Mode too because VS Code can retain the
 * contributed tool metadata there; invocation then returns `workspaceNotTrusted`
 * instead of failing with a missing implementation.
 */
export function registerAppHostLifecycleTools(service: AppHostLifecycleToolService): AppHostLifecycleToolRegistration {
    const registrations: vscode.Disposable[] = [];
    const startTool = new AppHostStartLanguageModelTool(service);
    const stopTool = new AppHostStopLanguageModelTool(service);
    // The preparable view exists for E2E automation, which only has raw JSON input. The
    // cast is safe because both tools validate every field of the input themselves and
    // treat anything unexpected as invalid rather than trusting the declared type.
    const tools = new Map<string, PreparableAppHostLifecycleTool>([
        [aspireAppHostStartToolName, { prepareInvocation: (options, token) => startTool.prepareInvocation({ input: options.input as unknown as AppHostStartToolInput }, token) }],
        [aspireAppHostStopToolName, { prepareInvocation: (options, token) => stopTool.prepareInvocation({ input: options.input as unknown as AppHostStopToolInput }, token) }],
    ]);
    const registerTools = () => {
        if (registrations.length > 0) {
            return;
        }

        registrations.push(
            vscode.lm.registerTool(aspireAppHostStartToolName, startTool),
            vscode.lm.registerTool(aspireAppHostStopToolName, stopTool));
        extensionLogOutputChannel.info('Registered Aspire AppHost lifecycle language model tools.');
    };

    if (typeof vscode.lm?.registerTool !== 'function') {
        extensionLogOutputChannel.info('Skipping Aspire AppHost lifecycle language model tools: the language model tool API is unavailable.');
    }
    else {
        registerTools();
    }

    return {
        get registered() {
            return registrations.length > 0;
        },
        tools,
        dispose() {
            registrations.forEach(registration => registration.dispose());
            registrations.length = 0;
        },
    };
}

function createToolResult(result: AppHostLifecycleToolResult): vscode.LanguageModelToolResult {
    return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(JSON.stringify(result))]);
}

function describeRequestedMode(value: unknown): string {
    return parseMode(value) ?? appHostLifecycleUnspecifiedMode;
}

function describeLaunchProfile(value: unknown): string | undefined {
    if (value === undefined) {
        return undefined;
    }

    return isValidLaunchProfile(value) ? escapeMarkdownForConfirmation(value) : appHostLifecycleInvalidLaunchProfile;
}
