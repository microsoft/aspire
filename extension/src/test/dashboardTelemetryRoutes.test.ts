import * as assert from 'assert';
import * as http from 'http';
import express from 'express';
import { AddressInfo } from 'net';
import { DashboardTelemetryPassthrough } from '../dcp/DashboardTelemetryPassthrough';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';

// Route-level integration tests for the dashboard → extension telemetry
// passthrough. The unit tests in `dashboardTelemetryPassthrough.test.ts`
// cover the pure helpers; this file pins the most important refactor
// guarantees that only show up once the Express handlers are wired:
//
//   1. The raw dashboard event name never becomes the extension event name
//      — it lives in `dashboard_event_name` on a fixed extension event.
//   2. `/telemetry/startOperation` returns the response shape the dashboard
//      `ReadFromJsonAsync<StartOperationResponse>` deserializer expects.
//   3. `_handleEnd` routes `Failure` / `UserFault` through
//      `sendTelemetryErrorEvent` rather than the regular channel.
//   4. End-without-matching-start is silently dropped (no thrown error,
//      no emitted event, HTTP 200).

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
    isError?: true;
}

class FakeReporter {
    public events: RecordedEvent[] = [];

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }
    sendTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isError: true });
    }
    sendDangerousTelemetryEvent(): void { /* not used */ }
    sendDangerousTelemetryErrorEvent(): void { /* not used */ }
    sendRawTelemetryEvent(): void { /* not used */ }
    dispose(): Promise<void> { return Promise.resolve(); }
}

interface Harness {
    server: http.Server;
    passthrough: DashboardTelemetryPassthrough;
    fake: FakeReporter;
    baseUrl: string;
    restore: () => void;
}

async function startHarness(): Promise<Harness> {
    const fake = new FakeReporter();
    const restoreReporter = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
    __resetCommonPropertiesForTests();

    const app = express();
    app.use(express.json({ limit: '1mb' }));
    const passthrough = new DashboardTelemetryPassthrough();
    // No-op auth — the test exercises the handlers, not the auth layer.
    passthrough.register(app, (_req, _res, next) => next());

    const server = http.createServer(app);
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
    const address = server.address() as AddressInfo;
    const baseUrl = `http://127.0.0.1:${address.port}`;

    return {
        server,
        passthrough,
        fake,
        baseUrl,
        restore: () => {
            restoreReporter();
            __resetCommonPropertiesForTests();
        },
    };
}

async function stopHarness(h: Harness): Promise<void> {
    h.passthrough.dispose();
    await new Promise<void>((resolve, reject) => {
        h.server.close(err => err ? reject(err) : resolve());
    });
    h.restore();
}

async function postJson(baseUrl: string, path: string, body: unknown): Promise<{ status: number; text: string }> {
    const res = await fetch(`${baseUrl}${path}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
    });
    const text = await res.text();
    return { status: res.status, text };
}

suite('DashboardTelemetryPassthrough route-level normalization', () => {
    let h: Harness;

    setup(async () => { h = await startHarness(); });
    teardown(async () => { await stopHarness(h); });

    test('POST /telemetry/operation normalizes the dashboard event name into dashboard_event_name', async () => {
        // The whole reason the registry exists: dashboard-supplied event
        // names like `aspire/dashboard/component/paramsset` must NOT become
        // extension event names (which would force a new classification row
        // per dashboard event). They live on a fixed `dashboard/operation`
        // event, with the raw name carried in a single classified property.
        const { status } = await postJson(h.baseUrl, '/telemetry/operation', {
            eventName: 'aspire/dashboard/component/paramsset',
            properties: {
                'aspire.dashboard.componentId': { value: 'metrics', propertyType: 1 },
            },
            result: 1,
        });
        assert.strictEqual(status, 200);
        assert.strictEqual(h.fake.events.length, 1);
        const event = h.fake.events[0];
        // Pin the contract: the extension event name is FIXED, the dashboard
        // event name is carried as a property.
        assert.strictEqual(event.name, 'dashboard/operation');
        assert.strictEqual(event.properties?.dashboard_event_name, 'aspire/dashboard/component/paramsset');
        assert.strictEqual(event.properties?.result, 'Success');
        assert.strictEqual(event.isError, undefined);
    });

    test('POST /telemetry/userTask emits dashboard/userTask, not the raw dashboard name', async () => {
        const { status } = await postJson(h.baseUrl, '/telemetry/userTask', {
            eventName: 'aspire/dashboard/mcp/toolcall',
            properties: {},
            result: 1,
        });
        assert.strictEqual(status, 200);
        assert.strictEqual(h.fake.events.length, 1);
        assert.strictEqual(h.fake.events[0].name, 'dashboard/userTask');
        assert.strictEqual(h.fake.events[0].properties?.dashboard_event_name, 'aspire/dashboard/mcp/toolcall');
    });

    test('POST /telemetry/fault always routes through the error channel', async () => {
        // Faults are the dashboard's exception telemetry channel; they MUST
        // go through sendTelemetryErrorEvent so the reporter applies its
        // stricter scrubbing pass.
        const { status } = await postJson(h.baseUrl, '/telemetry/fault', {
            eventName: 'aspire/dashboard/error',
            description: 'short fault description',
            severity: 3,
            properties: {},
        });
        assert.strictEqual(status, 200);
        assert.strictEqual(h.fake.events.length, 1);
        assert.strictEqual(h.fake.events[0].name, 'dashboard/fault');
        assert.strictEqual(h.fake.events[0].isError, true);
        assert.strictEqual(h.fake.events[0].properties?.fault_severity, 'Critical');
    });

    test('POST /telemetry/startOperation returns the camelCase StartOperationResponse shape', async () => {
        // The dashboard reads this body via
        // `ReadFromJsonAsync<StartOperationResponse>()`. The default options
        // for ReadFromJsonAsync are case-insensitive, but we still pin
        // camelCase here to keep the wire consistent with every other
        // endpoint in the passthrough.
        const { status, text } = await postJson(h.baseUrl, '/telemetry/startOperation', {
            eventName: 'aspire/dashboard/component/init',
            settings: {
                postStartEvent: true,
                startEventProperties: {},
                correlations: [],
            },
        });
        assert.strictEqual(status, 200);
        const body = JSON.parse(text);
        assert.strictEqual(typeof body.operationId, 'string');
        assert.ok(body.operationId.length > 0);
        assert.ok(body.correlation, 'expected correlation field');
        assert.strictEqual(typeof body.correlation.id, 'string');
        assert.strictEqual(body.correlation.eventType, 'Operation');
        // We must NOT leak PascalCase keys — they would silently get
        // deserialized when case-insensitive matching kicks in, hiding any
        // accidental property-name regression behind the dashboard's
        // tolerance. Pinning camelCase here forces test failures on drift.
        assert.strictEqual(body.OperationId, undefined);
        assert.strictEqual(body.Correlation, undefined);
    });

    test('POST /telemetry/startOperation + endOperation emits matched scope start/end events', async () => {
        const startRes = await postJson(h.baseUrl, '/telemetry/startOperation', {
            eventName: 'aspire/dashboard/component/paramsset',
            settings: {
                postStartEvent: true,
                startEventProperties: {
                    'aspire.dashboard.requestid': { value: 'req-123', propertyType: 1 },
                },
                correlations: [],
            },
        });
        const startBody = JSON.parse(startRes.text);
        const operationId = startBody.operationId as string;

        const endRes = await postJson(h.baseUrl, '/telemetry/endOperation', {
            id: operationId,
            result: 1,
        });
        assert.strictEqual(endRes.status, 200);

        // Exactly one start event + one end event.
        assert.strictEqual(h.fake.events.length, 2);
        const [startEvent, endEvent] = h.fake.events;
        assert.strictEqual(startEvent.name, 'dashboard/scope/start');
        assert.strictEqual(startEvent.properties?.dashboard_event_name, 'aspire/dashboard/component/paramsset');
        assert.strictEqual(startEvent.properties?.operation_id, operationId);
        assert.strictEqual(endEvent.name, 'dashboard/scope/end');
        assert.strictEqual(endEvent.properties?.operation_id, operationId);
        assert.strictEqual(endEvent.properties?.result, 'Success');
        // operation_id alone joins start↔end; dashboard_correlated_with must
        // NOT appear on scope/end (it would conflict semantically with the
        // same property on other events).
        assert.strictEqual(endEvent.properties?.dashboard_correlated_with, undefined);
        // duration_ms is a measurement, not a property.
        assert.ok(typeof endEvent.measurements?.duration_ms === 'number');
        assert.ok(endEvent.measurements!.duration_ms >= 0);
    });

    test('endOperation with Failure result routes through the error channel and drops the free-form error message', async () => {
        const startRes = await postJson(h.baseUrl, '/telemetry/startOperation', {
            eventName: 'aspire/dashboard/error',
            settings: { postStartEvent: false, startEventProperties: {}, correlations: [] },
        });
        const { operationId } = JSON.parse(startRes.text);

        // The dashboard's only producer of errorMessage passes a caught
        // exception's ex.Message (DashboardCommandExecutor.cs), which can embed
        // resource names / workspace paths. It must not be forwarded; the
        // Failure result label already carries the failure signal.
        await postJson(h.baseUrl, '/telemetry/endOperation', {
            id: operationId,
            result: 2, // Failure
            errorMessage: 'connect failed for resource my-secret-db at /Users/someone/repos/super-secret',
        });

        // postStartEvent: false → no start event emitted, just the end.
        assert.strictEqual(h.fake.events.length, 1);
        const event = h.fake.events[0];
        assert.strictEqual(event.name, 'dashboard/scope/end');
        assert.strictEqual(event.isError, true);
        assert.strictEqual(event.properties?.result, 'Failure');
        assert.strictEqual(event.properties?.error_message, undefined);
        // The raw message text must appear nowhere in the serialized event.
        const serialized = JSON.stringify(event);
        assert.ok(!serialized.includes('my-secret-db'), 'error message leaked into end event');
        assert.ok(!serialized.includes('super-secret'), 'workspace path leaked into end event');
    });

    test('endOperation without a matching start is silently dropped with HTTP 200', async () => {
        // The dashboard's send loop could occasionally call /endOperation for
        // an id we never saw (crash on the dashboard side, race during
        // restart, etc.). The contract is "respond 200, drop silently" so
        // the dashboard doesn't retry or surface noisy errors.
        const res = await postJson(h.baseUrl, '/telemetry/endOperation', {
            id: 'no-such-operation',
            result: 1,
        });
        assert.strictEqual(res.status, 200);
        assert.strictEqual(h.fake.events.length, 0);
    });

    test('startOperation with postStartEvent: false does not emit a start event but still allows end', async () => {
        // The pending operation is still tracked so the matching end-event
        // can be emitted. This is the dashboard-side opt-out for cases
        // where only the end-event (with the duration) is interesting.
        const startRes = await postJson(h.baseUrl, '/telemetry/startOperation', {
            eventName: 'aspire/dashboard/quiet-op',
            settings: { postStartEvent: false, startEventProperties: {}, correlations: [] },
        });
        const { operationId } = JSON.parse(startRes.text);
        assert.strictEqual(h.fake.events.length, 0, 'no start event should be emitted');

        await postJson(h.baseUrl, '/telemetry/endOperation', { id: operationId, result: 1 });
        assert.strictEqual(h.fake.events.length, 1);
        assert.strictEqual(h.fake.events[0].name, 'dashboard/scope/end');
    });

    test('GET /telemetry/enabled returns all three casing variants', async () => {
        // Emitting PascalCase / camelCase / snake_case is defense in depth —
        // see the route handler comment for the rationale. Pin the response
        // shape here so a future "cleanup" doesn't accidentally drop a
        // variant that some host parses with strict case sensitivity.
        const res = await fetch(`${h.baseUrl}/telemetry/enabled`);
        assert.strictEqual(res.status, 200);
        const body = await res.json() as { IsEnabled?: boolean; isEnabled?: boolean; is_enabled?: boolean };
        assert.ok('IsEnabled' in body);
        assert.ok('isEnabled' in body);
        assert.ok('is_enabled' in body);
        // The three must agree, otherwise a host that picks one over another
        // would observe inconsistent state.
        assert.strictEqual(body.IsEnabled, body.isEnabled);
        assert.strictEqual(body.IsEnabled, body.is_enabled);
    });

    test('PostFault drops exception message/stack-trace and never forwards the free-form description (privacy guarantee)', async () => {
        // The dashboard's TelemetryErrorRecorder builds the fault description as
        // `${ExceptionType}: ${exception.Message}` and forwards the message and
        // full stack trace as Basic-tagged properties — see
        // src/Aspire.Dashboard/Telemetry/TelemetryErrorRecorder.cs. All of these
        // embed user-chosen resource names and home-directory paths, so none may
        // leave the machine. The structured exception type/runtime version are
        // retained. This guards the README's "no workspace contents are
        // reported" claim along the actual request path.
        const secretMessage = 'Could not connect to my-secret-db at /Users/someone/repos/super-secret';
        const fakeStackTrace = 'at Foo() in C:\\\\Users\\\\someone\\\\repos\\\\super-secret\\\\Foo.cs:line 1\n'.repeat(50);
        await postJson(h.baseUrl, '/telemetry/fault', {
            eventName: 'aspire/dashboard/error',
            description: `System.InvalidOperationException: ${secretMessage}`,
            severity: 3,
            properties: {
                'Aspire.Dashboard.Exception.Message': { value: secretMessage, propertyType: 1 },
                'Aspire.Dashboard.Exception.StackTrace': { value: fakeStackTrace, propertyType: 1 },
                'Aspire.Dashboard.Exception.Type': { value: 'System.InvalidOperationException', propertyType: 1 },
                'Aspire.Dashboard.Exception.RuntimeVersion': { value: '10.0.0', propertyType: 1 },
            },
        });
        assert.strictEqual(h.fake.events.length, 1);
        const event = h.fake.events[0];
        // The free-form description must not be forwarded at all.
        assert.strictEqual(event.properties?.description, undefined);
        // And the raw secret text must appear nowhere in the serialized event.
        const serialized = JSON.stringify(event);
        assert.ok(!serialized.includes('my-secret-db'), 'exception message leaked into fault event');
        assert.ok(!serialized.includes('super-secret'), 'workspace path leaked into fault event');
        // Structured, non-sensitive exception dimensions are retained.
        const envelope = JSON.parse(event.properties?.dashboard_properties ?? '{}');
        assert.strictEqual(envelope.v['Aspire.Dashboard.Exception.Message'], undefined);
        assert.strictEqual(envelope.v['Aspire.Dashboard.Exception.StackTrace'], undefined);
        assert.strictEqual(envelope.v['Aspire.Dashboard.Exception.Type'], 'System.InvalidOperationException');
        assert.strictEqual(envelope.v['Aspire.Dashboard.Exception.RuntimeVersion'], '10.0.0');
    });

    test('PostFault drops unknown Basic-tagged Properties values', async () => {
        // Unknown Basic/UserSetting keys are free-form dashboard input. Drop
        // them instead of forwarding potentially sensitive workspace content.
        const longValue = 'x'.repeat(20_000);
        await postJson(h.baseUrl, '/telemetry/fault', {
            eventName: 'aspire/dashboard/error',
            description: 'short',
            severity: 3,
            properties: {
                'Aspire.Dashboard.SomeOtherDiagnostic': { value: longValue, propertyType: 1 },
                'Aspire.Dashboard.Version': { value: '10.0.0', propertyType: 1 },
            },
        });
        assert.strictEqual(h.fake.events.length, 1);
        const bundle = h.fake.events[0].properties?.dashboard_properties;
        assert.ok(bundle, 'expected dashboard_properties bundle');
        const envelope = JSON.parse(bundle);
        assert.strictEqual(envelope.v['Aspire.Dashboard.SomeOtherDiagnostic'], undefined);
        assert.strictEqual(envelope.v['Aspire.Dashboard.Version'], '10.0.0');
    });

    test('PostOperation clamps dashboard-supplied event names so a buggy upstream cannot leak long strings', async () => {
        // dashboard_event_name is forwarded verbatim onto the telemetry
        // event. A future dashboard regression that puts a workspace path or
        // user-controlled string into eventName would otherwise leak it at
        // full length. Defense-in-depth via clampDashboardKey.
        const longName = 'aspire/dashboard/' + 'x'.repeat(500);
        const { status } = await postJson(h.baseUrl, '/telemetry/operation', {
            eventName: longName,
            properties: {},
            result: 1,
        });
        assert.strictEqual(status, 200);
        const carried = h.fake.events[0].properties?.dashboard_event_name ?? '';
        assert.ok(carried.length < longName.length, 'expected event name to be clamped');
        assert.ok(carried.endsWith('...[truncated]'), 'expected truncation marker');
    });

    test('PostOperation with malformed correlatedWith does not crash the handler', async () => {
        // A non-array `correlatedWith` would previously crash `.map(...)` and
        // bubble out as Express 500. Reachable only through the auth layer
        // here, but the validation is cheap and prevents accidental drift.
        const { status } = await postJson(h.baseUrl, '/telemetry/operation', {
            eventName: 'aspire/dashboard/x',
            properties: {},
            result: 1,
            correlatedWith: 'not-an-array',
        });
        assert.strictEqual(status, 200);
        assert.strictEqual(h.fake.events.length, 1);
        // No dashboard_correlated_with should have been set.
        assert.strictEqual(h.fake.events[0].properties?.dashboard_correlated_with, undefined);
    });

    test('PostCommandLineFlags with malformed flagPrefixes does not crash the handler', async () => {
        const { status } = await postJson(h.baseUrl, '/telemetry/commandLineFlags', {
            flagPrefixes: 'not-an-array',
            additionalProperties: {},
        });
        assert.strictEqual(status, 200);
        assert.strictEqual(h.fake.events.length, 1);
        assert.strictEqual(h.fake.events[0].properties?.flag_prefixes, '');
    });

    test('PostProperty drops unknown property names', async () => {
        const longName = 'a'.repeat(2000);
        const { status } = await postJson(h.baseUrl, '/telemetry/property', {
            propertyName: longName,
            propertyValue: { value: 'ok', propertyType: 1 },
        });
        assert.strictEqual(status, 200);
        assert.strictEqual(h.fake.events.length, 0);
    });

    test('PostProperty carries known property names', async () => {
        const { status } = await postJson(h.baseUrl, '/telemetry/property', {
            propertyName: 'Aspire.Dashboard.Version',
            propertyValue: { value: '10.0.0', propertyType: 1 },
        });
        assert.strictEqual(status, 200);
        assert.strictEqual(h.fake.events.length, 1);
        assert.strictEqual(h.fake.events[0].properties?.property_name, 'Aspire.Dashboard.Version');
        assert.strictEqual(JSON.parse(h.fake.events[0].properties?.dashboard_properties ?? '').v['Aspire.Dashboard.Version'], '10.0.0');
    });

    test('PostAsset clamps over-long asset ids', async () => {
        const longId = 'b'.repeat(2000);
        await postJson(h.baseUrl, '/telemetry/asset', {
            eventName: 'aspire/dashboard/asset',
            assetId: longId,
            assetEventVersion: 1,
        });
        assert.strictEqual(h.fake.events.length, 1);
        const carried = h.fake.events[0].properties?.asset_id ?? '';
        assert.ok(carried.length < longId.length, 'expected asset id clamp');
    });
});
