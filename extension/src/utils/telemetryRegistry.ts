// ─────────────────────────────────────────────────────────────────────────────
// Telemetry event/property registry.
//
// Every event the extension emits has to be classified before it can ship,
// and the data-classification catalog stores **one row per
// (EntityName, PropertyName) pair**. That means a single new event with N
// properties creates N+1 rows that someone has to manually classify.
//
// The goal of this file is to make the surface area of telemetry the extension
// emits *exhaustively enumerable from one place*, so:
//
//   1. The data classification owner has a single file to audit when sweeping
//      for unclassified entries.
//   2. New events / properties can't be added without consciously touching the
//      registry. TypeScript enforces this — `sendTelemetryEvent` only accepts
//      event names that appear here, and only properties/measurements that
//      appear under the matching event entry.
//   3. Producers that forward telemetry from outside the extension (notably
//      the Aspire dashboard, which sends arbitrary event names and arbitrary
//      property keys over HTTP) are normalized to a small fixed set of event
//      names before they ever reach the reporter. The producer-supplied names
//      live in stable property fields (e.g. `dashboard_event_name`,
//      `dashboard_properties` as a JSON blob), so the classification footprint
//      stays constant as the producer adds new instrumentation upstream.
//
// IMPORTANT for reviewers: changing the keys of this object (or the unions
// inside each entry) requires a corresponding classification update before
// the build ships. The build does not enforce this; reviewers do.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Properties merged into every event by {@link setCommonTelemetryProperties}.
 *
 * These are intentionally enumerated here (rather than living implicitly in
 * the `commonProperties` map in telemetry.ts) because the classification
 * catalog tracks (event, property) pairs — every common property duplicates
 * into a row for every event. The set is deliberately tiny.
 */
export type CommonTelemetryProperty = 'apphost_languages' | 'apphost_present';

/**
 * Per-event schema. Each entry lists the event-specific properties and
 * measurements the event is permitted to carry. {@link CommonTelemetryProperty}
 * values are implicitly merged in by the {@link sendTelemetryEvent} wrapper
 * and need not be repeated here.
 *
 * Use `never` for `properties` or `measurements` when the event has no
 * event-specific entries of that kind.
 */
export interface TelemetryEventSchema {
    // ── Extension-emitted events ────────────────────────────────────────────
    'command/invoked': {
        properties: 'command' | 'outcome' | 'source' | 'error_kind';
        measurements: 'duration_ms';
    };
    'engagement/active': {
        properties: 'trigger' | 'has_csharp_devkit';
        measurements: 'workspace_folders';
    };
    'runningapphostsview/shown': {
        properties: 'view_mode' | 'initial_visibility';
        measurements: 'running_apphosts' | 'total_resources';
    };
    'debug/apphost/start': {
        properties: 'mode' | 'apphost_language' | 'apphost_is_directory' | 'command';
        measurements: never;
    };
    'debug/apphost/end': {
        properties: 'mode' | 'apphost_language' | 'ended_with_error' | 'distinct_resource_types';
        measurements: 'duration_ms' | 'total_child_sessions' | 'distinct_resource_type_count';
    };
    'debug/runsession/start': {
        properties: 'resource_type' | 'debugger_extension_matched' | 'mode';
        measurements: never;
    };
    'debug/runsession/end': {
        properties: 'resource_type' | 'mode' | 'exit_code_bucket' | 'end_reason' | 'error_kind';
        measurements: 'duration_ms' | 'exit_code';
    };

    // ── Dashboard passthrough events ────────────────────────────────────────
    // The Aspire dashboard sends arbitrary telemetry over HTTP. Rather than
    // forwarding the dashboard's event names verbatim (which would mean every
    // new dashboard event = a new classification row), we collapse each HTTP
    // endpoint kind onto a single extension event name. The original dashboard
    // event name lives in `dashboard_event_name`; arbitrary properties /
    // metrics live in the JSON-encoded `dashboard_properties` and
    // `dashboard_measurements` string properties.
    //
    // Net effect: the classification catalog only needs the rows listed here,
    // regardless of how many distinct events / properties the dashboard adds
    // upstream.
    'dashboard/operation': {
        properties:
            | 'dashboard_event_name'
            | 'dashboard_properties'
            | 'dashboard_measurements'
            | 'dashboard_correlated_with'
            | 'result';
        measurements: never;
    };
    'dashboard/usertask': {
        properties:
            | 'dashboard_event_name'
            | 'dashboard_properties'
            | 'dashboard_measurements'
            | 'dashboard_correlated_with'
            | 'result';
        measurements: never;
    };
    'dashboard/fault': {
        properties:
            | 'dashboard_event_name'
            | 'dashboard_properties'
            | 'dashboard_measurements'
            | 'dashboard_correlated_with'
            | 'fault_severity';
        measurements: never;
    };
    'dashboard/asset': {
        properties:
            | 'dashboard_event_name'
            | 'dashboard_properties'
            | 'dashboard_measurements'
            | 'dashboard_correlated_with'
            | 'asset_id'
            | 'asset_event_version';
        measurements: never;
    };
    'dashboard/scope/start': {
        properties:
            | 'dashboard_event_name'
            | 'dashboard_properties'
            | 'dashboard_measurements'
            | 'dashboard_correlated_with'
            | 'operation_id'
            | 'scope_kind';
        measurements: never;
    };
    'dashboard/scope/end': {
        properties:
            | 'dashboard_event_name'
            | 'dashboard_properties'
            | 'dashboard_measurements'
            | 'operation_id'
            | 'scope_kind'
            | 'result';
        // duration_ms is computed at the end-event call site so it's the only
        // first-class measurement (vs the JSON-bundled `dashboard_measurements`
        // which preserves the start-time bag).
        //
        // Intentionally no `dashboard_correlated_with` here: `operation_id`
        // already joins start↔end events, and on every other event
        // `dashboard_correlated_with` means "this event is correlated WITH
        // these others" — reusing it on end-events to carry the operation's
        // OWN minted correlation id would give the same column two different
        // meanings that downstream queries couldn't disambiguate.
        measurements: 'duration_ms';
    };
    'dashboard/property/set': {
        properties: 'property_name' | 'dashboard_properties' | 'dashboard_measurements';
        measurements: never;
    };
    'dashboard/property/recurring': {
        properties: 'property_name' | 'dashboard_properties' | 'dashboard_measurements';
        measurements: never;
    };
    'dashboard/commandlineflags': {
        properties: 'flag_prefixes' | 'dashboard_properties' | 'dashboard_measurements';
        measurements: never;
    };
}

/** Union of every event name the extension is allowed to emit. */
export type KnownTelemetryEventName = keyof TelemetryEventSchema;

/**
 * Property bag accepted by {@link sendTelemetryEvent} for a given event name.
 * The set is the event's own properties plus {@link CommonTelemetryProperty}
 * (which are merged in by the wrapper).
 *
 * Using `Partial<Record<...>>` keeps each property optional while binding the
 * key set to the registry. Assignments to unknown keys (e.g. `props.foo = ...`)
 * are rejected by the type checker.
 */
export type EventProperties<E extends KnownTelemetryEventName> =
    Partial<Record<TelemetryEventSchema[E]['properties'] | CommonTelemetryProperty, string>>;

/**
 * Numeric measurement bag accepted by {@link sendTelemetryEvent} for a given
 * event name. Common-properties have no measurement equivalent — measurements
 * are always event-specific.
 *
 * The `[X] extends [never] ? never : ...` conditional is deliberate: a naive
 * `Partial<Record<never, number>>` collapses to `{}`, which TypeScript treats
 * as "any non-null object" and would silently admit arbitrary measurement
 * keys. Wrapping in single-tuple brackets prevents distributive evaluation,
 * and resolving to `never` for events that declare no measurements forces
 * such call sites to omit the argument entirely.
 */
export type EventMeasurements<E extends KnownTelemetryEventName> =
    [TelemetryEventSchema[E]['measurements']] extends [never]
        ? never
        : Partial<Record<TelemetryEventSchema[E]['measurements'], number>>;

/**
 * Property bag accepted by {@link setCommonTelemetryProperties}. Restricted to
 * the registered common-property keys so a typo can't silently add a row to
 * the classification catalog for every event.
 */
export type CommonTelemetryProperties = Partial<Record<CommonTelemetryProperty, string | undefined>>;
