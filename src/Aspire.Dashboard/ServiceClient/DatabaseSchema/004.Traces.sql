-- Licensed to the .NET Foundation under one or more agreements.
-- The .NET Foundation licenses this file to you under the MIT license.

CREATE TABLE IF NOT EXISTS telemetry_traces (
    trace_id TEXT PRIMARY KEY,
    first_span_timestamp_ticks INTEGER NOT NULL,
    last_span_end_timestamp_ticks INTEGER NOT NULL,
    duration_ticks INTEGER NOT NULL,
    last_updated_timestamp_ticks INTEGER NOT NULL,
    full_name TEXT NOT NULL,
    primary_span_id TEXT NOT NULL,
    has_error INTEGER NOT NULL CHECK (has_error IN (0, 1)),
    has_gen_ai INTEGER NOT NULL CHECK (has_gen_ai IN (0, 1))
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_spans (
    trace_id TEXT NOT NULL REFERENCES telemetry_traces(trace_id) ON DELETE CASCADE,
    span_id TEXT NOT NULL,
    parent_span_id TEXT NULL,
    resource_id INTEGER NOT NULL REFERENCES telemetry_resources(resource_id) ON DELETE CASCADE,
    resource_view_id INTEGER NOT NULL REFERENCES telemetry_resource_views(resource_view_id) ON DELETE CASCADE,
    scope_id INTEGER NOT NULL REFERENCES telemetry_scopes(scope_id),
    name TEXT NOT NULL,
    kind INTEGER NOT NULL,
    start_time_ticks INTEGER NOT NULL,
    end_time_ticks INTEGER NOT NULL,
    status INTEGER NOT NULL,
    status_message TEXT NULL,
    trace_state TEXT NULL,
    uninstrumented_peer_resource_id INTEGER NULL REFERENCES telemetry_resources(resource_id) ON DELETE SET NULL,
    resource_order_ticks INTEGER NOT NULL,
    PRIMARY KEY (trace_id, span_id)
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_trace_resources (
    trace_id TEXT NOT NULL REFERENCES telemetry_traces(trace_id) ON DELETE CASCADE,
    resource_id INTEGER NOT NULL REFERENCES telemetry_resources(resource_id) ON DELETE CASCADE,
    resource_order_ticks INTEGER NOT NULL,
    total_spans INTEGER NOT NULL CHECK (total_spans > 0),
    errored_spans INTEGER NOT NULL CHECK (errored_spans >= 0 AND errored_spans <= total_spans),
    PRIMARY KEY (trace_id, resource_id)
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_span_attributes (
    trace_id TEXT NOT NULL,
    span_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    attribute_key TEXT NOT NULL,
    attribute_value TEXT NOT NULL,
    PRIMARY KEY (trace_id, span_id, ordinal),
    FOREIGN KEY (trace_id, span_id) REFERENCES telemetry_spans(trace_id, span_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_span_events (
    event_id TEXT PRIMARY KEY,
    trace_id TEXT NOT NULL,
    span_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    event_name TEXT NOT NULL,
    event_time_ticks INTEGER NOT NULL,
    UNIQUE (trace_id, span_id, ordinal),
    FOREIGN KEY (trace_id, span_id) REFERENCES telemetry_spans(trace_id, span_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_span_event_attributes (
    event_id TEXT NOT NULL REFERENCES telemetry_span_events(event_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    attribute_key TEXT NOT NULL,
    attribute_value TEXT NOT NULL,
    PRIMARY KEY (event_id, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_span_links (
    link_id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_trace_id TEXT NOT NULL,
    source_span_id TEXT NOT NULL,
    target_trace_id TEXT NOT NULL,
    target_span_id TEXT NOT NULL,
    trace_state TEXT NOT NULL,
    FOREIGN KEY (source_trace_id, source_span_id) REFERENCES telemetry_spans(trace_id, span_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_span_link_attributes (
    link_id INTEGER NOT NULL REFERENCES telemetry_span_links(link_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    attribute_key TEXT NOT NULL,
    attribute_value TEXT NOT NULL,
    PRIMARY KEY (link_id, ordinal)
) STRICT;

CREATE INDEX IF NOT EXISTS ix_telemetry_traces_order
    ON telemetry_traces(first_span_timestamp_ticks, trace_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_spans_resource_order
    ON telemetry_spans(resource_id, start_time_ticks, trace_id, span_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_spans_trace_order
    ON telemetry_spans(trace_id, start_time_ticks, span_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_spans_parent
    ON telemetry_spans(trace_id, parent_span_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_trace_resources_order
    ON telemetry_trace_resources(trace_id, resource_order_ticks, resource_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_span_attributes_owner_key
    ON telemetry_span_attributes(trace_id, span_id, attribute_key);
CREATE INDEX IF NOT EXISTS ix_telemetry_span_attributes_key_value_owner
    ON telemetry_span_attributes(attribute_key COLLATE NOCASE, attribute_value COLLATE NOCASE, trace_id, span_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_span_links_target
    ON telemetry_span_links(target_trace_id, target_span_id);