import * as vscode from 'vscode';

import {
    editorAssistanceOpenDashboardInvocationMessage,
    editorAssistanceOpenOutputConfirmationMessage,
    editorAssistanceOpenOutputConfirmationTitle,
    editorAssistanceOpenOutputInvocationMessage,
} from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    aspireDebugSessionStatusToolName,
    aspireExplainLaunchFailureToolName,
    aspireHotReloadStatusToolName,
    aspireListDebugSessionsToolName,
    aspireOpenDashboardToolName,
    aspireOpenOutputToolName,
    type DebugSessionStatusToolInput,
    type EditorAssistanceToolRegistration,
    type EditorAssistanceToolResult,
    type ExplainLaunchFailureToolInput,
    type HotReloadStatusToolInput,
    type ListDebugSessionsToolInput,
    type OpenDashboardToolInput,
    type OpenOutputToolInput,
} from './editorAssistanceToolContracts';
import { EditorAssistanceToolService } from './editorAssistanceToolService';
import { EditorAssistanceTelemetry } from './editorAssistanceTelemetry';
import { escapeMarkdown } from './languageModelToolUi';

export class AspireDebugSessionStatusLanguageModelTool implements vscode.LanguageModelTool<DebugSessionStatusToolInput> {
    constructor(
        private readonly _service: EditorAssistanceToolService,
        private readonly _telemetry: EditorAssistanceTelemetry = new EditorAssistanceTelemetry()) {
    }

    async invoke(
        options: vscode.LanguageModelToolInvocationOptions<DebugSessionStatusToolInput>,
        token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._telemetry.capture(
            aspireDebugSessionStatusToolName,
            () => this._service.getDebugSessionStatus(options.input, token)));
    }
}

export class AspireExplainLaunchFailureLanguageModelTool implements vscode.LanguageModelTool<ExplainLaunchFailureToolInput> {
    constructor(
        private readonly _service: EditorAssistanceToolService,
        private readonly _telemetry: EditorAssistanceTelemetry = new EditorAssistanceTelemetry()) {
    }

    async invoke(
        options: vscode.LanguageModelToolInvocationOptions<ExplainLaunchFailureToolInput>,
        token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._telemetry.capture(
            aspireExplainLaunchFailureToolName,
            () => this._service.explainLaunchFailure(options.input, token)));
    }
}

export class AspireOpenDashboardLanguageModelTool implements vscode.LanguageModelTool<OpenDashboardToolInput> {
    constructor(
        private readonly _service: EditorAssistanceToolService,
        private readonly _telemetry: EditorAssistanceTelemetry = new EditorAssistanceTelemetry()) {
    }

    async prepareInvocation(
        options: vscode.LanguageModelToolInvocationPrepareOptions<OpenDashboardToolInput>,
        token: vscode.CancellationToken): Promise<vscode.PreparedToolInvocation> {
        // Opening the Dashboard is a read-only handoff to a view the user already owns, so it
        // runs without a confirmation prompt. Preparation only resolves the display path for the
        // progress message; the target that actually gets opened is resolved again inside
        // `invoke`, so there is no confirmed-target state for a later call to consume.
        const displayPath = await this._service.prepareDashboardTargetDisplayPath(options.input?.appHostPath, token);
        return {
            invocationMessage: editorAssistanceOpenDashboardInvocationMessage(escapeMarkdown(displayPath)),
        };
    }

    async invoke(
        options: vscode.LanguageModelToolInvocationOptions<OpenDashboardToolInput>,
        token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._telemetry.capture(
            aspireOpenDashboardToolName,
            () => this._service.openDashboard(options.input, token)));
    }
}

export class AspireOpenOutputLanguageModelTool implements vscode.LanguageModelTool<OpenOutputToolInput> {
    constructor(
        private readonly _service: EditorAssistanceToolService,
        private readonly _telemetry: EditorAssistanceTelemetry = new EditorAssistanceTelemetry()) {
    }

    async prepareInvocation(
        _options: vscode.LanguageModelToolInvocationPrepareOptions<OpenOutputToolInput>,
        _token: vscode.CancellationToken): Promise<vscode.PreparedToolInvocation> {
        return {
            invocationMessage: editorAssistanceOpenOutputInvocationMessage,
            confirmationMessages: {
                title: editorAssistanceOpenOutputConfirmationTitle,
                message: editorAssistanceOpenOutputConfirmationMessage,
            },
        };
    }

    async invoke(
        options: vscode.LanguageModelToolInvocationOptions<OpenOutputToolInput>,
        token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._telemetry.capture(
            aspireOpenOutputToolName,
            () => this._service.openOutput(options.input, token)));
    }
}

export class AspireListDebugSessionsLanguageModelTool implements vscode.LanguageModelTool<ListDebugSessionsToolInput> {
    constructor(
        private readonly _service: EditorAssistanceToolService,
        private readonly _telemetry: EditorAssistanceTelemetry = new EditorAssistanceTelemetry()) {
    }

    async invoke(
        options: vscode.LanguageModelToolInvocationOptions<ListDebugSessionsToolInput>,
        token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._telemetry.capture(
            aspireListDebugSessionsToolName,
            () => this._service.listDebugSessions(options.input, token)));
    }
}

export class AspireHotReloadStatusLanguageModelTool implements vscode.LanguageModelTool<HotReloadStatusToolInput> {
    constructor(
        private readonly _service: EditorAssistanceToolService,
        private readonly _telemetry: EditorAssistanceTelemetry = new EditorAssistanceTelemetry()) {
    }

    async invoke(
        options: vscode.LanguageModelToolInvocationOptions<HotReloadStatusToolInput>,
        token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._telemetry.capture(
            aspireHotReloadStatusToolName,
            () => this._service.getHotReloadStatus(options.input, token)));
    }
}

/**
 * Registers editor-assistance tools when the stable language model tool API exists.
 *
 * Status, explanation, session listing, and Hot Reload reporting are read-only and
 * intentionally expose only `invoke`. Dashboard and Output handoff change editor UI, so
 * those two adapters also implement `prepareInvocation`.
 *
 * Only Output confirms. The rule is what the handoff reveals: opening the Dashboard is a
 * read-only handoff to a surface the user already owns a command and a tree-view button for,
 * and the result never returns its URL, whereas the Output view surfaces log content and takes
 * over an editor panel.
 */
export function registerEditorAssistanceTools(
    service: EditorAssistanceToolService,
    telemetry: EditorAssistanceTelemetry = new EditorAssistanceTelemetry()): EditorAssistanceToolRegistration {
    const registrations: vscode.Disposable[] = [];
    const statusTool = new AspireDebugSessionStatusLanguageModelTool(service, telemetry);
    const explainTool = new AspireExplainLaunchFailureLanguageModelTool(service, telemetry);
    const dashboardTool = new AspireOpenDashboardLanguageModelTool(service, telemetry);
    const outputTool = new AspireOpenOutputLanguageModelTool(service, telemetry);
    const listTool = new AspireListDebugSessionsLanguageModelTool(service, telemetry);
    const hotReloadTool = new AspireHotReloadStatusLanguageModelTool(service, telemetry);
    const tools = new Map<string, vscode.LanguageModelTool<unknown>>([
        [aspireDebugSessionStatusToolName, statusTool as vscode.LanguageModelTool<unknown>],
        [aspireExplainLaunchFailureToolName, explainTool as vscode.LanguageModelTool<unknown>],
        [aspireOpenDashboardToolName, dashboardTool as vscode.LanguageModelTool<unknown>],
        [aspireOpenOutputToolName, outputTool as vscode.LanguageModelTool<unknown>],
        [aspireListDebugSessionsToolName, listTool as vscode.LanguageModelTool<unknown>],
        [aspireHotReloadStatusToolName, hotReloadTool as vscode.LanguageModelTool<unknown>],
    ]);

    if (typeof vscode.lm?.registerTool !== 'function') {
        extensionLogOutputChannel.info('Skipping Aspire editor assistance language model tools: the language model tool API is unavailable.');
    }
    else {
        registrations.push(
            vscode.lm.registerTool(aspireDebugSessionStatusToolName, statusTool),
            vscode.lm.registerTool(aspireExplainLaunchFailureToolName, explainTool),
            vscode.lm.registerTool(aspireOpenDashboardToolName, dashboardTool),
            vscode.lm.registerTool(aspireOpenOutputToolName, outputTool),
            vscode.lm.registerTool(aspireListDebugSessionsToolName, listTool),
            vscode.lm.registerTool(aspireHotReloadStatusToolName, hotReloadTool));
        extensionLogOutputChannel.info('Registered Aspire editor assistance language model tools.');
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

function createToolResult(result: EditorAssistanceToolResult): vscode.LanguageModelToolResult {
    return new vscode.LanguageModelToolResult([
        new vscode.LanguageModelTextPart(JSON.stringify(result)),
    ]);
}
