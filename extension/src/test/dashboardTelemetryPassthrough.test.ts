import * as assert from 'assert';
import { __testOnly__ } from '../dcp/DashboardTelemetryPassthrough';

const {
    bundleDashboardData,
    formatCorrelations,
    formatFlagPrefixes,
    clampDashboardKey,
    telemetryResultLabel,
    faultSeverityLabel,
    isFailureResult,
    scrubFreeformDiagnosticText,
    formatAssetEventVersion,
    MAX_DIAGNOSTIC_STRING_LENGTH,
    MAX_BUNDLE_CHARS,
    MAX_DASHBOARD_KEY_LENGTH,
    MAX_CORRELATIONS,
    MAX_FLAG_PREFIXES,
} = __testOnly__;

const PropertyType = {
    Pii: 0 as const,
    Basic: 1 as const,
    Metric: 2 as const,
    UserSetting: 3 as const,
};

suite('DashboardTelemetryPassthrough.bundleDashboardData', () => {
    test('returns empty object for undefined input', () => {
        assert.deepStrictEqual(bundleDashboardData(undefined), {});
    });

    test('returns empty object when every input entry is dropped (no leaking sentinel)', () => {
        // Inputs of all Pii / null / undefined should result in no bundle
        // fields at all — we do NOT want to emit an empty `{}` blob or a
        // truncation marker, because both would falsely advertise that
        // something was sent.
        const result = bundleDashboardData({
            email: { value: 'user@example.com', propertyType: PropertyType.Pii },
            absent: { value: null, propertyType: PropertyType.Basic },
        });
        assert.deepStrictEqual(result, {});
    });

    test('bundles invariant-culture string Metric values into dashboard_measurements', () => {
        // Dashboard emits metric values via int.ToString(CultureInfo.InvariantCulture);
        // see src/Aspire.Dashboard/Components/Pages/Metrics.razor.cs and
        // StructuredLogs.razor.cs. The contract is "tag is Metric, payload is a
        // string" — the bundler must route them to the measurements bundle.
        const result = bundleDashboardData({
            'Aspire.Dashboard.StructuredLogs.FilterCount': { value: '3', propertyType: PropertyType.Metric },
            'Aspire.Dashboard.Metrics.InstrumentsCount': { value: '-1', propertyType: PropertyType.Metric },
        });
        assert.strictEqual(result.properties, undefined);
        assert.deepStrictEqual(JSON.parse(result.measurements ?? ''), {
            v: {
                'Aspire.Dashboard.StructuredLogs.FilterCount': 3,
                'Aspire.Dashboard.Metrics.InstrumentsCount': -1,
            },
        });
    });

    test('bundles raw numeric Metric values into dashboard_measurements', () => {
        const result = bundleDashboardData({
            'Aspire.Dashboard.StructuredLogs.FilterCount': { value: 42, propertyType: PropertyType.Metric },
        });
        assert.deepStrictEqual(JSON.parse(result.measurements ?? ''), { v: { 'Aspire.Dashboard.StructuredLogs.FilterCount': 42 } });
        assert.strictEqual(result.properties, undefined);
    });

    test('Metric value that cannot be coerced to a number is dropped', () => {
        const result = bundleDashboardData({
            'Aspire.Dashboard.StructuredLogs.FilterCount': { value: 'not-a-number', propertyType: PropertyType.Metric },
        });
        assert.strictEqual(result.measurements, undefined);
        assert.strictEqual(result.properties, undefined);
    });

    test('non-Metric numeric values are NOT promoted to measurements', () => {
        // Pre-fix behavior promoted any number to a measurement, which mis-bucketed
        // basic numeric dimensions; this test pins the contract.
        const result = bundleDashboardData({
            'Aspire.Dashboard.AIAssistant.ChatMessageCount': { value: 42, propertyType: PropertyType.Basic },
        });
        assert.strictEqual(result.measurements, undefined);
        assert.deepStrictEqual(JSON.parse(result.properties ?? ''), { v: { 'Aspire.Dashboard.AIAssistant.ChatMessageCount': '42' } });
    });

    test('Pii-tagged properties are dropped end-to-end', () => {
        // Verifies the privacy guarantee: even though the dashboard wraps
        // values in AspireTelemetryProperty(Pii=0), nothing PII-tagged makes
        // it into either bundle.
        const result = bundleDashboardData({
            email: { value: 'user@example.com', propertyType: PropertyType.Pii },
            'Aspire.Dashboard.StructuredLogs.FilterCount': { value: 1, propertyType: PropertyType.Metric },
        });
        const props = result.properties ? JSON.parse(result.properties) : { v: {} };
        const measurements = result.measurements ? JSON.parse(result.measurements) : { v: {} };
        assert.strictEqual(props.v.email, undefined);
        assert.deepStrictEqual(measurements, { v: { 'Aspire.Dashboard.StructuredLogs.FilterCount': 1 } });
    });

    test('stringifies known booleans and string arrays inside dashboard_properties', () => {
        const result = bundleDashboardData({
            'Aspire.Dashboard.ConsoleLogs.ShowTimestamp': { value: true, propertyType: PropertyType.Basic },
            'Aspire.Dashboard.AIAssistant.Enabled': { value: false, propertyType: PropertyType.Basic },
            'Aspire.Dashboard.Resource.Types': { value: ['project', 'container'], propertyType: PropertyType.Basic },
        });
        const envelope = JSON.parse(result.properties ?? '');
        assert.strictEqual(envelope.v['Aspire.Dashboard.ConsoleLogs.ShowTimestamp'], 'true');
        assert.strictEqual(envelope.v['Aspire.Dashboard.AIAssistant.Enabled'], 'false');
        assert.strictEqual(envelope.v['Aspire.Dashboard.Resource.Types'], '["project","container"]');
        assert.strictEqual(envelope.t, undefined);
    });

    test('skips null and undefined values', () => {
        const result = bundleDashboardData({
            a: { value: null, propertyType: PropertyType.Basic },
            b: { value: undefined, propertyType: PropertyType.Basic },
            'Aspire.Dashboard.Version': { value: 'present', propertyType: PropertyType.Basic },
        });
        assert.deepStrictEqual(JSON.parse(result.properties ?? ''), { v: { 'Aspire.Dashboard.Version': 'present' } });
        assert.strictEqual(result.measurements, undefined);
    });

    test('PascalCase input (legacy assumption) is intentionally ignored', () => {
        // The dashboard's `PostAsJsonAsync` defaults to camelCase on the wire
        // (see DashboardTelemetryPassthrough.ts file header for the citation
        // chain). PascalCase payloads are not produced by the dashboard and
        // we explicitly do not coerce them — coercing would mask a regression
        // that broke the actual contract.
        const pascalInput = {
            ignored: { Value: 'no', PropertyType: PropertyType.Basic },
        } as unknown as Parameters<typeof bundleDashboardData>[0];
        const result = bundleDashboardData(pascalInput);
        assert.deepStrictEqual(result, {});
    });

    test('drops unknown Basic/UserSetting properties', () => {
        const result = bundleDashboardData({
            secret: { value: 'connection-string=secret', propertyType: PropertyType.Basic },
            customSetting: { value: 'private-path', propertyType: PropertyType.UserSetting },
            'Aspire.Dashboard.Version': { value: '10.0.0', propertyType: PropertyType.Basic },
        });
        const envelope = JSON.parse(result.properties ?? '');
        assert.strictEqual(envelope.v.secret, undefined);
        assert.strictEqual(envelope.v.customSetting, undefined);
        assert.strictEqual(envelope.v['Aspire.Dashboard.Version'], '10.0.0');
    });

    test('drops unknown Metric properties before key names can leak', () => {
        const result = bundleDashboardData({
            '/Users/alice/repo/secret': { value: '1', propertyType: PropertyType.Metric },
            'Aspire.Dashboard.StructuredLogs.FilterCount': { value: '3', propertyType: PropertyType.Metric },
        });
        const envelope = JSON.parse(result.measurements ?? '');
        assert.strictEqual(envelope.v['/Users/alice/repo/secret'], undefined);
        assert.strictEqual(envelope.v['Aspire.Dashboard.StructuredLogs.FilterCount'], 3);
    });

    test('per-entry truncation caps a single huge value before it reaches the bundle', () => {
        // The per-entry cap (MAX_DIAGNOSTIC_STRING_LENGTH) is applied
        // uniformly to every string value before bundling — see the privacy
        // mitigation note on bundleDashboardData. So a single huge value
        // doesn't trigger bundle-level char-cap truncation; it gets capped
        // to ~1KB + the truncation marker and lands in the bundle normally.
        const huge = 'x'.repeat(MAX_BUNDLE_CHARS * 2);
        const result = bundleDashboardData({
            'Aspire.Dashboard.Version': { value: 'ok', propertyType: PropertyType.Basic },
            'Aspire.Dashboard.BuildId': { value: huge, propertyType: PropertyType.Basic },
        });
        const blob = result.properties ?? '';
        assert.ok(blob.length <= MAX_BUNDLE_CHARS, `expected blob ≤ ${MAX_BUNDLE_CHARS} chars, got ${blob.length}`);
        const envelope = JSON.parse(blob);
        // No bundle-level truncation: both entries are present and `t` is unset.
        assert.strictEqual(envelope.t, undefined);
        assert.strictEqual(envelope.v['Aspire.Dashboard.Version'], 'ok');
        assert.ok(typeof envelope.v['Aspire.Dashboard.BuildId'] === 'string', 'big value should be present in bundle');
        // But the value was truncated per-entry — it has the per-entry
        // truncation marker appended.
        assert.ok((envelope.v['Aspire.Dashboard.BuildId'] as string).endsWith('...[truncated]'),
            `expected per-entry truncation marker on big value, got ${(envelope.v['Aspire.Dashboard.BuildId'] as string).slice(-30)}`);
    });

    test('bundle char-cap truncation kicks in when many capped entries cumulatively exceed the budget', () => {
        // After per-entry truncation each value is at most ~1KB. The bundle
        // cap is 8KB, so several large-ish entries together can still exceed
        // the bundle budget. Verify the bundle-level char-cap loop drops
        // trailing entries and sets the envelope `t` flag.
        const propertyKeys = [
            'Aspire.Dashboard.Version',
            'Aspire.Dashboard.BuildId',
            'Aspire.Dashboard.ComponentId',
            'Aspire.Dashboard.ComponentType',
            'Aspire.Dashboard.UserAgent',
            'Aspire.Dashboard.Metrics.SelectedDuration',
            'Aspire.Dashboard.Metrics.SelectedView',
            'Aspire.Dashboard.Exception.Type',
            'Aspire.Dashboard.Exception.RuntimeVersion',
            'Aspire.Dashboard.Resource.Type',
            'Aspire.Dashboard.Resource.View',
            'Aspire.Dashboard.RequestId',
        ];
        const flood: { [key: string]: { value: string; propertyType: 1 } } = {};
        for (const key of propertyKeys) {
            flood[key] = { value: 'x'.repeat(MAX_BUNDLE_CHARS * 2), propertyType: PropertyType.Basic };
        }
        const result = bundleDashboardData(flood);
        const blob = result.properties ?? '';
        assert.ok(blob.length <= MAX_BUNDLE_CHARS, `expected blob ≤ ${MAX_BUNDLE_CHARS} chars, got ${blob.length}`);
        const envelope = JSON.parse(blob);
        assert.strictEqual(envelope.t, true, 'expected envelope truncation flag');
        assert.ok(Object.keys(envelope.v).length < propertyKeys.length, `expected some entries dropped, got ${Object.keys(envelope.v).length}`);
        assert.ok(Object.keys(envelope.v).length > 0, 'expected at least one entry kept');
        // Truncation drops trailing entries first.
        assert.strictEqual(envelope.v['Aspire.Dashboard.Version']?.endsWith('...[truncated]'), true);
    });

    test('keeps allowed measurements independent from properties', () => {
        const flood: { [key: string]: { value: string; propertyType: 1 | 2 } } = {};
        flood['Aspire.Dashboard.StructuredLogs.FilterCount'] = { value: '5', propertyType: PropertyType.Metric };
        flood['Aspire.Dashboard.Version'] = { value: 'present', propertyType: PropertyType.Basic };
        const result = bundleDashboardData(flood);
        const measEnvelope = JSON.parse(result.measurements ?? '');
        assert.deepStrictEqual(measEnvelope, { v: { 'Aspire.Dashboard.StructuredLogs.FilterCount': 5 } });
        const propEnvelope = JSON.parse(result.properties ?? '');
        assert.strictEqual(propEnvelope.v['Aspire.Dashboard.Version'], 'present');
        assert.strictEqual(propEnvelope.t, undefined);
    });

    test('drops unknown dashboard measurement literally named __truncated__', () => {
        const result = bundleDashboardData({
            __truncated__: { value: 1, propertyType: PropertyType.Metric },
            'Aspire.Dashboard.Metrics.InstrumentsCount': { value: 2, propertyType: PropertyType.Metric },
        });
        const envelope = JSON.parse(result.measurements ?? '');
        assert.strictEqual(envelope.v.__truncated__, undefined);
        assert.strictEqual(envelope.v['Aspire.Dashboard.Metrics.InstrumentsCount'], 2);
        assert.strictEqual(envelope.t, undefined);
    });

    test('preserves observed key order for allowed measurements', () => {
        const result = bundleDashboardData({
            'Aspire.Dashboard.Metrics.InstrumentsCount': { value: '1', propertyType: PropertyType.Metric },
            'Aspire.Dashboard.StructuredLogs.FilterCount': { value: '2', propertyType: PropertyType.Metric },
        });
        const envelope = JSON.parse(result.measurements ?? '');
        assert.deepStrictEqual(Object.keys(envelope.v), [
            'Aspire.Dashboard.Metrics.InstrumentsCount',
            'Aspire.Dashboard.StructuredLogs.FilterCount',
        ]);
    });

    test('drops over-long unknown Metric keys so a buggy dashboard cannot smuggle PII through key names', () => {
        const longKey = 'k' + 'a'.repeat(MAX_DASHBOARD_KEY_LENGTH + 200);
        const result = bundleDashboardData({
            [longKey]: { value: 1, propertyType: PropertyType.Metric },
        });
        assert.strictEqual(result.measurements, undefined);
    });

    test('rejects non-object input shapes (defensive)', () => {
        // The C# request types declare a Dictionary<string, AspireTelemetryProperty>,
        // but a malformed payload could send an array or a primitive. Either
        // would iterate via Object.entries with numeric or per-character
        // keys; treating them as empty input avoids polluting the bundle.
        assert.deepStrictEqual(bundleDashboardData([] as unknown as undefined), {});
        assert.deepStrictEqual(bundleDashboardData('oops' as unknown as undefined), {});
        assert.deepStrictEqual(bundleDashboardData(42 as unknown as undefined), {});
    });

    test('drops Basic-tagged exception message and stack trace keys (workspace-content leak)', () => {
        // The dashboard's TelemetryErrorRecorder tags exception message and
        // stack trace as Basic, not Pii, but their values embed user-chosen
        // resource names, interpolated strings, home-directory paths, and
        // user-assembly type names. They must never leave the machine even
        // though they pass the Pii filter. Structured exception dimensions are
        // retained. Keys mirror TelemetryPropertyKeys.cs.
        const result = bundleDashboardData({
            'Aspire.Dashboard.Exception.Message': { value: 'Could not connect to my-secret-db', propertyType: PropertyType.Basic },
            'Aspire.Dashboard.Exception.StackTrace': { value: '   at MyApp.Secret.Thing() in /Users/alice/proj/Foo.cs:line 42', propertyType: PropertyType.Basic },
            'Aspire.Dashboard.Exception.Type': { value: 'System.InvalidOperationException', propertyType: PropertyType.Basic },
            'Aspire.Dashboard.Exception.RuntimeVersion': { value: '10.0.0', propertyType: PropertyType.Basic },
        });
        const props = JSON.parse(result.properties ?? '{}').v;
        assert.strictEqual(props['Aspire.Dashboard.Exception.Message'], undefined);
        assert.strictEqual(props['Aspire.Dashboard.Exception.StackTrace'], undefined);
        assert.strictEqual(props['Aspire.Dashboard.Exception.Type'], 'System.InvalidOperationException');
        assert.strictEqual(props['Aspire.Dashboard.Exception.RuntimeVersion'], '10.0.0');
    });

    test('drops the console-logs resource-name key (defense-in-depth against workspace content)', () => {
        // Resource names are user-chosen workspace content. The dashboard
        // declares this key but does not wire a Basic sender to it today; drop
        // it pre-emptively so a future regression can't leak resource names.
        const result = bundleDashboardData({
            'Aspire.Dashboard.ConsoleLogs.ResourceName': { value: 'my-private-service', propertyType: PropertyType.Basic },
            'Aspire.Dashboard.Exception.Type': { value: 'System.Exception', propertyType: PropertyType.Basic },
        });
        const props = JSON.parse(result.properties ?? '{}').v;
        assert.strictEqual(props['Aspire.Dashboard.ConsoleLogs.ResourceName'], undefined);
        assert.strictEqual(props['Aspire.Dashboard.Exception.Type'], 'System.Exception');
    });

    test('honors the Pii discriminator even when sent as a stringified number', () => {
        // propertyType is only *typed* as the enum; a JSON sender can send the
        // string "0" for Pii. A strict === check would let it slip past the Pii
        // drop, so the discriminator is coerced through Number().
        const result = bundleDashboardData({
            email: { value: 'user@example.com', propertyType: '0' as unknown as 0 },
            'Aspire.Dashboard.Version': { value: 'safe', propertyType: PropertyType.Basic },
        });
        const props = JSON.parse(result.properties ?? '{}').v;
        assert.strictEqual(props.email, undefined);
        assert.strictEqual(props['Aspire.Dashboard.Version'], 'safe');
    });
});

suite('DashboardTelemetryPassthrough.formatCorrelations', () => {
    test('returns undefined for empty or missing list', () => {
        assert.strictEqual(formatCorrelations(undefined), undefined);
        assert.strictEqual(formatCorrelations([]), undefined);
    });

    test('returns undefined when input is not an array (defensive: payload from untrusted source)', () => {
        // The dashboard's TelemetryEventCorrelation[] is the contract, but a
        // bug or malicious sender could produce a string, an object, etc.
        // Without the Array.isArray guard, the legacy `correlations.length`
        // check would pass for strings and crash on .map(...).
        assert.strictEqual(formatCorrelations('not-an-array'), undefined);
        assert.strictEqual(formatCorrelations({ length: 1 } as unknown), undefined);
        assert.strictEqual(formatCorrelations(null), undefined);
        assert.strictEqual(formatCorrelations(42), undefined);
    });

    test('skips malformed entries (missing or non-string id/eventType)', () => {
        // The helper accepts unknown[]-ish input and must drop anything that
        // doesn't match the wire shape rather than crashing or emitting
        // `undefined:undefined`.
        const result = formatCorrelations([
            { id: 'a', eventType: 'Operation' },
            null,
            { id: 'b' },                            // missing eventType
            { eventType: 'UserTask' },              // missing id
            { id: 1, eventType: 'Operation' },      // non-string id
            { id: 'c', eventType: 42 },             // non-string eventType
            'oops',                                  // primitive
            { id: 'd', eventType: 'UserTask' },
        ]);
        assert.strictEqual(result, 'Operation:a;UserTask:d');
    });

    test('joins eventType:id pairs with semicolons in input order', () => {
        // This is the wire format we emit for the `dashboard_correlated_with`
        // property. Changing it is a breaking change for any downstream
        // analytics that parses the field, so pin it here.
        const result = formatCorrelations([
            { id: 'a', eventType: 'Operation' },
            { id: 'b', eventType: 'UserTask' },
        ]);
        assert.strictEqual(result, 'Operation:a;UserTask:b');
    });

    test('caps the formatted list at MAX_CORRELATIONS to prevent unbounded growth', () => {
        // A malicious sender could otherwise pour 100k correlations into a
        // single event and bypass the per-bundle caps (correlations are not
        // bundled — they sit directly on the property value).
        const input = Array.from({ length: MAX_CORRELATIONS + 50 }, (_, i) => ({
            id: `id-${i}`,
            eventType: 'Operation' as const,
        }));
        const result = formatCorrelations(input);
        const pairs = (result ?? '').split(';');
        assert.strictEqual(pairs.length, MAX_CORRELATIONS);
        assert.strictEqual(pairs[0], 'Operation:id-0');
        assert.strictEqual(pairs[MAX_CORRELATIONS - 1], `Operation:id-${MAX_CORRELATIONS - 1}`);
    });

    test('strips wire-format delimiters from element values', () => {
        // If the dashboard ever sent an id containing ';' or ':', the parser
        // on the receiving side would mis-split. Strip them so the format
        // stays unambiguous.
        const result = formatCorrelations([
            { id: 'a:b;c', eventType: 'Op:eration;X' as unknown as 'Operation' },
        ]);
        assert.strictEqual(result, 'Op_eration_X:a_b_c');
    });

    test('clamps over-long element values (no per-element length cap previously)', () => {
        // Only the *count* (MAX_CORRELATIONS) was bounded; a single huge id or
        // eventType (within the express.json() body limit) would otherwise ship
        // verbatim. Clamp each element to MAX_DASHBOARD_KEY_LENGTH like every
        // other dashboard-supplied field.
        const longId = 'i'.repeat(MAX_DASHBOARD_KEY_LENGTH + 200);
        const result = formatCorrelations([{ id: longId, eventType: 'Operation' }]);
        const [, id] = (result ?? '').split(':');
        assert.ok(id.endsWith('...[truncated]'), 'expected truncation marker on id');
        assert.strictEqual(id.length, MAX_DASHBOARD_KEY_LENGTH);
    });

    test('bounds the total serialized size, not just the per-element/count caps', () => {
        // 100 entries x ~257 chars could otherwise produce a ~26 KB property,
        // well over the AppInsights per-property cap. The running total must be
        // bounded by MAX_BUNDLE_CHARS.
        const many = Array.from({ length: MAX_CORRELATIONS }, () => ({
            id: 'i'.repeat(MAX_DASHBOARD_KEY_LENGTH + 50),
            eventType: 'Operation',
        }));
        const result = formatCorrelations(many) ?? '';
        assert.ok(result.length <= MAX_BUNDLE_CHARS, `expected <= ${MAX_BUNDLE_CHARS}, got ${result.length}`);
        assert.ok(result.length > 0, 'expected at least one correlation to be emitted');
    });
});

suite('DashboardTelemetryPassthrough.clampDashboardKey', () => {
    test('returns short strings unchanged', () => {
        assert.strictEqual(clampDashboardKey('short.key'), 'short.key');
        assert.strictEqual(clampDashboardKey(''), '');
    });

    test('truncates over-long strings and appends marker', () => {
        const long = 'a'.repeat(MAX_DASHBOARD_KEY_LENGTH + 100);
        const result = clampDashboardKey(long);
        assert.ok(result.endsWith('...[truncated]'), 'expected truncation marker');
        assert.strictEqual(result.length, MAX_DASHBOARD_KEY_LENGTH);
    });

    test('returns empty string for non-string input (defensive)', () => {
        assert.strictEqual(clampDashboardKey(42 as unknown as string), '');
        assert.strictEqual(clampDashboardKey(null as unknown as string), '');
    });
});

suite('DashboardTelemetryPassthrough.formatFlagPrefixes', () => {
    test('returns empty string for non-array input', () => {
        // The C# request record types this as string[], but a malformed JSON
        // payload could send anything. Don't crash.
        assert.strictEqual(formatFlagPrefixes(undefined), '');
        assert.strictEqual(formatFlagPrefixes('not-an-array'), '');
        assert.strictEqual(formatFlagPrefixes(null), '');
        assert.strictEqual(formatFlagPrefixes({ length: 1 }), '');
    });

    test('joins string entries with commas', () => {
        assert.strictEqual(formatFlagPrefixes(['--foo', '--bar']), '--foo,--bar');
    });

    test('skips non-string entries', () => {
        assert.strictEqual(formatFlagPrefixes(['--foo', 42, null, '--bar']), '--foo,--bar');
    });

    test('caps the count at MAX_FLAG_PREFIXES', () => {
        const input = Array.from({ length: MAX_FLAG_PREFIXES + 25 }, (_, i) => `--flag${i}`);
        const result = formatFlagPrefixes(input);
        const items = result.split(',');
        assert.strictEqual(items.length, MAX_FLAG_PREFIXES);
        assert.strictEqual(items[0], '--flag0');
    });

    test('strips embedded commas from individual entries (anti-smuggling)', () => {
        // A single entry containing commas would otherwise let an attacker
        // synthesize extra apparent prefixes on the receiving side.
        assert.strictEqual(formatFlagPrefixes(['--a,--b', '--c']), '--a_--b,--c');
    });

    test('truncates over-long entries via clampDashboardKey', () => {
        const long = '--' + 'x'.repeat(MAX_DASHBOARD_KEY_LENGTH + 50);
        const result = formatFlagPrefixes([long]);
        assert.ok(result.endsWith('...[truncated]'));
    });

    test('strips flag values, keeping only the flag name (=, :, and whitespace separators)', () => {
        // This endpoint is a network boundary. A buggy or hostile sender could
        // pass full arguments instead of bare prefixes; the value half can
        // carry secrets (tokens, passwords, connection strings) and must never
        // reach the first-class flag_prefixes property. Only the name survives.
        assert.strictEqual(formatFlagPrefixes(['--token=secret']), '--token');
        assert.strictEqual(formatFlagPrefixes(['--password:hunter2']), '--password');
        assert.strictEqual(formatFlagPrefixes(['--key value']), '--key');
        assert.strictEqual(formatFlagPrefixes(['--a=1', '--b:2']), '--a,--b');
    });

    test('skips entries that are empty once the value is stripped', () => {
        // A leading separator leaves no name portion; drop it rather than
        // emitting an empty token.
        assert.strictEqual(formatFlagPrefixes(['=onlyvalue', '--ok']), '--ok');
    });
});

suite('DashboardTelemetryPassthrough enum label mappers', () => {
    test('telemetryResultLabel maps numeric wire values to readable labels', () => {
        // Mirrors `TelemetryResult` in VisualStudioTelemetryTypes.cs. The enum
        // has no [JsonStringEnumConverter], so the dashboard sends numbers.
        assert.strictEqual(telemetryResultLabel(0), 'None');
        assert.strictEqual(telemetryResultLabel(1), 'Success');
        assert.strictEqual(telemetryResultLabel(2), 'Failure');
        assert.strictEqual(telemetryResultLabel(3), 'UserFault');
        assert.strictEqual(telemetryResultLabel(4), 'UserCancel');
    });

    test('telemetryResultLabel returns Unknown sentinel for missing or out-of-range values', () => {
        assert.strictEqual(telemetryResultLabel(undefined), 'Unknown');
        assert.strictEqual(telemetryResultLabel(99 as 0), 'Unknown(99)');
    });

    test('isFailureResult routes Failure and UserFault to the error channel', () => {
        // Anchors the routing contract for sendTelemetryErrorEvent vs sendTelemetryEvent.
        // UserCancel is routine UX and should stay in the standard channel.
        assert.strictEqual(isFailureResult(0), false); // None
        assert.strictEqual(isFailureResult(1), false); // Success
        assert.strictEqual(isFailureResult(2), true);  // Failure
        assert.strictEqual(isFailureResult(3), true);  // UserFault
        assert.strictEqual(isFailureResult(4), false); // UserCancel
        assert.strictEqual(isFailureResult('2' as unknown as 0), true);
        assert.strictEqual(isFailureResult(undefined), false);
    });

    test('telemetryResultLabel coerces non-numeric JSON input instead of interpolating it verbatim', () => {
        // The wire field is only *typed* as an int; a hostile/buggy sender can
        // send a string or array. These must NOT leak verbatim into the label.
        assert.strictEqual(telemetryResultLabel('2' as unknown as 0), 'Failure');
        assert.strictEqual(telemetryResultLabel([1, 2, 'leak'] as unknown as 0), 'Unknown');
        assert.strictEqual(telemetryResultLabel('arbitrary workspace text' as unknown as 0), 'Unknown');
        assert.strictEqual(telemetryResultLabel(null as unknown as 0), 'Unknown');
        assert.strictEqual(telemetryResultLabel({} as unknown as 0), 'Unknown');
    });

    test('faultSeverityLabel coerces non-numeric JSON input instead of interpolating it verbatim', () => {
        assert.strictEqual(faultSeverityLabel('3' as unknown as 0), 'Critical');
        assert.strictEqual(faultSeverityLabel(['x', 'secret'] as unknown as 0), 'Unknown');
        assert.strictEqual(faultSeverityLabel(99 as unknown as 0), 'Unknown(99)');
        assert.strictEqual(faultSeverityLabel('boom' as unknown as 0), 'Unknown');
    });

    test('formatAssetEventVersion coerces non-numeric JSON input to a bounded string', () => {
        // assetEventVersion is typed `int` upstream; coerce so an arbitrary
        // string/array can't ship verbatim into asset_event_version.
        assert.strictEqual(formatAssetEventVersion(3), '3');
        assert.strictEqual(formatAssetEventVersion('5'), '5');
        assert.strictEqual(formatAssetEventVersion('leaked workspace value'), 'unknown');
        assert.strictEqual(formatAssetEventVersion([1, 2, 'secret']), 'unknown');
        assert.strictEqual(formatAssetEventVersion(undefined), 'unknown');
        assert.strictEqual(formatAssetEventVersion({}), 'unknown');
    });
});

suite('DashboardTelemetryPassthrough.scrubFreeformDiagnosticText', () => {
    test('returns empty string for undefined input', () => {
        assert.strictEqual(scrubFreeformDiagnosticText(undefined), '');
    });

    test('passes through text below the limit unchanged', () => {
        const text = 'A short error message.';
        assert.strictEqual(scrubFreeformDiagnosticText(text), text);
    });

    test('truncates long text and appends a marker so receivers can detect it', () => {
        const longText = 'x'.repeat(MAX_DIAGNOSTIC_STRING_LENGTH + 500);
        const scrubbed = scrubFreeformDiagnosticText(longText);
        assert.ok(scrubbed.endsWith('...[truncated]'), `expected truncation marker, got ${scrubbed.slice(-30)}`);
        assert.strictEqual(scrubbed.length, MAX_DIAGNOSTIC_STRING_LENGTH);
    });

    test('returns empty string for non-string input without throwing (untrusted JSON body)', () => {
        // The request field is only *typed* as a string; a malformed/hostile
        // body can send any JSON value. Before the guard, `.slice` on a
        // non-string threw a TypeError that surfaced as an Express 500 (and on
        // the endOperation path, after the pending entry was already deleted —
        // silently dropping an in-flight start). All of these must coerce to ''.
        assert.strictEqual(scrubFreeformDiagnosticText({} as unknown as string), '');
        assert.strictEqual(scrubFreeformDiagnosticText([] as unknown as string), '');
        assert.strictEqual(scrubFreeformDiagnosticText(42 as unknown as string), '');
        assert.strictEqual(scrubFreeformDiagnosticText(null as unknown as string), '');
        assert.strictEqual(scrubFreeformDiagnosticText(true as unknown as string), '');
    });
});
