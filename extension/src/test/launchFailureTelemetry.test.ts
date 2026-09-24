import * as assert from 'assert';

import {
    LaunchFailureStore,
    sendLaunchFailureRecordedTelemetry,
    type LaunchFailureRecordedTelemetryEvent,
    type SanitizedLaunchFailure,
} from '../services/launchFailureStore';
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
            measurements: { store_size: 7 },
        }]);
        assertTelemetryOmits(events, sentinels);
    });

    test('emits once after an accepted write with the maintained global size', () => {
        const accepted: Array<{ failure: SanitizedLaunchFailure; storeSize: number }> = [];
        const store = new LaunchFailureStore(
            { now: () => 10_000 },
            (failure, storeSize) => accepted.push({ failure, storeSize }));
        const failure: SanitizedLaunchFailure = {
            stage: 'build',
            category: 'buildFailed',
            controller: 'cli',
            mode: 'run',
            providerKind: 'dotnet',
            exitCodeBucket: 'one',
        };

        for (let index = 1; index <= 51; index++) {
            store.record(`apphost-${index}` as OpaqueAppHostIdentity, failure);
        }

        assert.strictEqual(accepted.length, 51);
        assert.deepStrictEqual(accepted[50], {
            failure,
            storeSize: 50,
        });

        store.read('apphost-51' as OpaqueAppHostIdentity);
        store.clear();
        assert.strictEqual(accepted.length, 51, 'Reads, capacity maintenance, and clear must not double-emit.');
    });

    test('a replacement emits a new event without increasing retained AppHost count', () => {
        const accepted: Array<{ failure: SanitizedLaunchFailure; storeSize: number }> = [];
        const store = new LaunchFailureStore(
            undefined,
            (failure, storeSize) => accepted.push({ failure, storeSize }));
        const first: SanitizedLaunchFailure = {
            stage: 'build',
            category: 'buildFailed',
            controller: 'cli',
            mode: 'run',
            providerKind: 'dotnet',
            exitCodeBucket: 'one',
        };
        const latest: SanitizedLaunchFailure = { ...first, stage: 'cliLaunch', category: 'processExited' };
        const identity = 'apphost-1' as OpaqueAppHostIdentity;
        store.record(identity, first);
        store.record(identity, latest);

        assert.deepStrictEqual(accepted, [
            { failure: first, storeSize: 1 },
            { failure: latest, storeSize: 1 },
        ]);
        assert.deepStrictEqual(store.read(identity), latest);
    });

    test('telemetry independently revalidates unsafe fields and bounds its measurement', () => {
        const failure = {
            stage: 'unsafe-stage',
            category: 'unsafe-category',
            controller: 'unsafe-controller',
            mode: 'unsafe-mode',
            providerKind: 'unsafe-provider',
            exitCodeBucket: 'unsafe-exit-code',
        } as unknown as SanitizedLaunchFailure;

        for (const [size, expected] of [[-1, 0], [3.9, 3], [51, 50], [NaN, 0], [Infinity, 0]]) {
            const events: LaunchFailureRecordedTelemetryEvent[] = [];
            sendLaunchFailureRecordedTelemetry(failure, size, (eventName, properties, measurements) => {
                events.push({ eventName, properties, measurements });
            });
            assert.deepStrictEqual(events, [{
                eventName: 'aspire/vscode/launchfailure/recorded',
                properties: {
                    stage: 'debugSession',
                    category: 'unknown',
                    controller: 'editor',
                    mode: 'other',
                    provider_kind: 'other',
                    exit_code_bucket: 'none',
                },
                measurements: { store_size: expected },
            }]);
        }
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
