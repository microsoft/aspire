import * as assert from 'assert';

import {
    LaunchFailureJournal,
    sendLaunchFailureRecordedTelemetry,
    type LaunchFailureRecordedTelemetryEvent,
    type SanitizedLaunchFailure,
} from '../services/launchFailureJournal';
import { type OpaqueAppHostIdentity } from '../utils/appHostIdentity';

suite('launch failure telemetry', () => {
    test('records only sanitized bounded failure fields', () => {
        const events: LaunchFailureRecordedTelemetryEvent[] = [];
        const sentinels = [
            '/private/AppHost.csproj',
            'resource-secret',
            'https://dashboard-secret.example',
            'raw-error-secret',
            'session-secret',
            '44123',
            '--credential=secret',
            'PRIVATE_ENV=secret',
            'unsafe_path_key',
        ];
        const failure = {
            stage: 'debugSession',
            category: 'permissionDenied',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'node',
            exitCodeBucket: 'other',
            appHostPath: sentinels[0],
            resourceName: sentinels[1],
            dashboardUrl: sentinels[2],
            rawError: sentinels[3],
            sessionId: sentinels[4],
            pid: sentinels[5],
            args: [sentinels[6]],
            env: { PRIVATE_ENV: sentinels[7] },
            unsafe_path_key: sentinels[0],
        } as unknown as SanitizedLaunchFailure;

        sendLaunchFailureRecordedTelemetry(
            failure,
            7,
            (eventName, properties, measurements) => {
                events.push({ eventName, properties, measurements });
            });

        assert.deepStrictEqual(events, [{
            eventName: 'aspire/vscode/launchfailure/recorded',
            properties: {
                stage: 'debugSession',
                category: 'permissionDenied',
                controller: 'editor',
                mode: 'debug',
                provider_kind: 'node',
                exit_code_bucket: 'other',
            },
            measurements: { journal_size: 7 },
        }]);
        assertTelemetryOmits(events, sentinels);
    });

    test('emits once after an accepted write with the maintained global size', () => {
        const accepted: Array<{ failure: SanitizedLaunchFailure; journalSize: number }> = [];
        const journal = new LaunchFailureJournal(
            { now: () => 10_000 },
            (failure, journalSize) => accepted.push({ failure, journalSize }));
        const failure: SanitizedLaunchFailure = {
            stage: 'build',
            category: 'buildFailed',
            controller: 'cli',
            mode: 'run',
            providerKind: 'dotnet',
            exitCodeBucket: 'one',
        };

        for (let index = 1; index <= 51; index++) {
            journal.record(`apphost-${index}` as OpaqueAppHostIdentity, failure);
        }

        assert.strictEqual(accepted.length, 51);
        assert.deepStrictEqual(accepted[50], {
            failure,
            journalSize: 50,
        });

        journal.readLatest();
        journal.readLatest('apphost-51' as OpaqueAppHostIdentity);
        journal.clear();
        assert.strictEqual(accepted.length, 51, 'Reads, capacity maintenance, and clear must not double-emit.');
    });
});

function assertTelemetryOmits(events: readonly LaunchFailureRecordedTelemetryEvent[], sentinels: readonly string[]): void {
    const serialized = JSON.stringify(events);
    for (const sentinel of sentinels) {
        assert.strictEqual(
            serialized.includes(sentinel),
            false,
            `Telemetry contained unsafe sentinel '${sentinel}'. Payload: ${serialized}`);
    }
}
