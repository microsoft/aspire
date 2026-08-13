import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
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

    public telemetryLevel: 'all' | 'error' | 'crash' | 'off' = 'all';

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        // Extension code now bypasses this path; recording here would only
        // see a regression to the prefixed channel. Kept as a typed no-op
        // so the fake still satisfies the TelemetryReporter shape.
    }

    sendTelemetryErrorEvent(): void { /* not used here */ }

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }
    sendRawTelemetryEvent(): void { /* not used here */ }

    dispose(): Promise<void> { return Promise.resolve(); }
}

suite('MeaningfulEngagementReporter', () => {
    let fake: FakeTelemetryReporter;
    let restoreReporter: () => void;
    const tempDirs: string[] = [];
    const tempParent = join(process.cwd(), '.test-tmp');

    function makeTempDir(): string {
        mkdirSync(tempParent, { recursive: true });
        const dir = mkdtempSync(join(tempParent, 'meaningful-engagement-'));
        tempDirs.push(dir);
        return dir;
    }

    setup(() => {
        fake = new FakeTelemetryReporter();
        restoreReporter = __setReporterForTests(fake as unknown as TelemetryReporter);
        __resetCommonPropertiesForTests();
    });

    teardown(() => {
        sinon.restore();
        for (const dir of tempDirs) {
            if (existsSync(dir)) {
                rmSync(dir, { recursive: true, force: true });
            }
        }
        tempDirs.length = 0;
        restoreReporter();
        __resetCommonPropertiesForTests();
    });

    suiteTeardown(() => {
        if (existsSync(tempParent)) {
            rmSync(tempParent, { recursive: true, force: true });
        }
    });

    test('includes AppHost target versions with AppHost language telemetry', async () => {
        const workspacePath = makeTempDir();
        const appHostPath = join(workspacePath, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
        const workspaceFolder = {
            uri: vscode.Uri.file(workspacePath),
            name: 'workspace',
            index: 0,
        } as vscode.WorkspaceFolder;
        const candidates: CandidateAppHostDisplayInfo[] = [{
            path: appHostPath,
            language: 'csharp',
            status: 'buildable',
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

            assert.strictEqual(fake.events[0].name, 'aspire/vscode/engagement/active');
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
