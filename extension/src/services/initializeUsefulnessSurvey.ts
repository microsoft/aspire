import * as vscode from 'vscode';
import { noLabel, usefulnessSurveyNeverAgain, usefulnessSurveyPrompt, yesLabel } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    getActiveCommandCount, isExtensionUsageTelemetryEnabled,
    onDidChangeExtensionUsageTelemetryEnabled, onDidInvokeCommand, sendTelemetryEvent,
} from '../utils/telemetry';
import { AppHostLaunchService } from './AppHostLaunchService';
import { UsefulnessSurveyService, usefulnessSurveyCampaign, type UsefulnessSurveyOutcome } from './UsefulnessSurveyService';

export async function showUsefulnessSurvey(): Promise<UsefulnessSurveyOutcome> {
    const actions: (vscode.MessageItem & { outcome: UsefulnessSurveyOutcome })[] = [
        { title: yesLabel, outcome: 'yes' },
        { title: noLabel, outcome: 'no' },
        { title: usefulnessSurveyNeverAgain, outcome: 'never_again' },
    ];
    const selection = await vscode.window.showInformationMessage(usefulnessSurveyPrompt, ...actions);
    return selection?.outcome ?? 'dismissed';
}

export function initializeUsefulnessSurvey(context: vscode.ExtensionContext, launchService: AppHostLaunchService): void {
    if (!usefulnessSurveyCampaign.enabled || context.extensionMode !== vscode.ExtensionMode.Production) {
        return;
    }
    const service = new UsefulnessSurveyService(
        context.globalState,
        {
            canCollect: () => isExtensionUsageTelemetryEnabled() &&
                vscode.workspace.getConfiguration('telemetry').get<boolean>('feedback.enabled', true),
            canShow: () => vscode.window.state.focused && getActiveCommandCount() === 0 &&
                launchService.launchingPaths.length === 0 && launchService.pendingLifecycleOperationCount === 0 &&
                !launchService.hasPendingOrActiveNonRunOperation,
            schedule: (callback, delayMs) => {
                const timer = setTimeout(callback, delayMs);
                return new vscode.Disposable(() => clearTimeout(timer));
            },
            show: showUsefulnessSurvey,
            send: (kind, outcome) => {
                const properties = {
                    campaign_id: usefulnessSurveyCampaign.id,
                    question_id: usefulnessSurveyCampaign.questionId,
                };
                if (kind === 'invitation') {
                    sendTelemetryEvent('aspire/vscode/survey/invitation', properties);
                }
                else if (outcome !== undefined) {
                    sendTelemetryEvent('aspire/vscode/survey/result', { ...properties, outcome });
                }
            },
            warn: () => extensionLogOutputChannel.warn('Aspire usefulness survey stopped because its state or notification could not be processed.'),
        },
        usefulnessSurveyCampaign);
    context.subscriptions.push(
        service,
        onDidInvokeCommand(event => service.recordCommand(event.command)),
        onDidChangeExtensionUsageTelemetryEnabled(() => service.permissionsChanged()),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('telemetry.feedback.enabled')) {
                service.permissionsChanged();
            }
        }));
}
