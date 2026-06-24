import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import type { CandidateAppHostDisplayInfo, AppHostDiscoveryService } from '../utils/appHostDiscovery';
import { MeaningfulEngagementReporter } from '../utils/meaningfulEngagement';
import { __resetCommonPropertiesForTests, __setReporterForTests, getCommonTelemetryProperties } from '../utils/telemetry';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
}

class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendTelemetryErrorEvent(): void { /* not used here */ }
    sendDangerousTelemetryEvent(): void { /* not used here */ }
    sendDangerousTelemetryErrorEvent(): void { /* not used here */ }
    sendRawTelemetryEvent(): void { /* not used here */ }

    dispose(): Promise<void> { return Promise.resolve(); }
}

suite('MeaningfulEngagementReporter', () => {
    let fake: FakeTelemetryReporter;
    let restoreReporter: () => void;

    setup(() => {
        fake = new FakeTelemetryReporter();
        restoreReporter = __setReporterForTests(fake as unknown as TelemetryReporter);
        __resetCommonPropertiesForTests();
    });

    teardown(() => {
        sinon.restore();
        restoreReporter();
        __resetCommonPropertiesForTests();
    });

    test('includes AppHost target versions with AppHost language telemetry', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        } as vscode.WorkspaceFolder;
        const candidates: CandidateAppHostDisplayInfo[] = [{
            path: '/workspace/AppHost/AppHost.csproj',
            language: 'csharp',
            status: 'buildable',
            aspireHostingVersion: '13.5.0',
        }];
        const discovery = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => candidates,
        } as unknown as AppHostDiscoveryService;
        sinon.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);

        const reporter = new MeaningfulEngagementReporter(discovery);
        try {
            reporter.recordCommandInvoked();
            await waitFor(() => fake.events.length === 1);

            assert.strictEqual(fake.events[0].name, 'engagement/active');
            assert.strictEqual(fake.events[0].properties?.apphost_languages, 'csharp');
            assert.strictEqual(fake.events[0].properties?.apphost_target_versions, '13.5.0');
            assert.strictEqual(getCommonTelemetryProperties().apphost_target_versions, '13.5.0');
        }
        finally {
            reporter.dispose();
        }
    });
});

async function waitFor(predicate: () => boolean): Promise<void> {
    const start = Date.now();
    while (!predicate()) {
        if (Date.now() - start > 1000) {
            throw new Error('Timed out waiting for condition.');
        }

        await new Promise(resolve => setTimeout(resolve, 10));
    }
}
