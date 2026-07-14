-- Licensed to the .NET Foundation under one or more agreements.
-- The .NET Foundation licenses this file to you under the MIT license.

CREATE TABLE IF NOT EXISTS telemetry_resources (
    resource_id INTEGER PRIMARY KEY AUTOINCREMENT,
    resource_name TEXT NOT NULL,
    instance_id TEXT NOT NULL,
    instance_id_is_null INTEGER NOT NULL,
    uninstrumented_peer INTEGER NOT NULL DEFAULT 0,
    has_logs INTEGER NOT NULL DEFAULT 0,
    has_traces INTEGER NOT NULL DEFAULT 0,
    has_metrics INTEGER NOT NULL DEFAULT 0,
    UNIQUE (resource_name, instance_id_is_null, instance_id)
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_resource_views (
    resource_view_id INTEGER PRIMARY KEY AUTOINCREMENT,
    resource_id INTEGER NOT NULL REFERENCES telemetry_resources(resource_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_resource_view_attributes (
    resource_view_id INTEGER NOT NULL REFERENCES telemetry_resource_views(resource_view_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    attribute_key TEXT NOT NULL,
    attribute_value TEXT NOT NULL,
    PRIMARY KEY (resource_view_id, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_scopes (
    scope_id INTEGER PRIMARY KEY AUTOINCREMENT,
    scope_name TEXT NOT NULL UNIQUE,
    scope_version TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_scope_attributes (
    scope_id INTEGER NOT NULL REFERENCES telemetry_scopes(scope_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    attribute_key TEXT NOT NULL,
    attribute_value TEXT NOT NULL,
    PRIMARY KEY (scope_id, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_logs (
    log_id INTEGER PRIMARY KEY AUTOINCREMENT,
    resource_id INTEGER NOT NULL REFERENCES telemetry_resources(resource_id) ON DELETE CASCADE,
    resource_view_id INTEGER NOT NULL REFERENCES telemetry_resource_views(resource_view_id) ON DELETE CASCADE,
    scope_id INTEGER NOT NULL REFERENCES telemetry_scopes(scope_id),
    timestamp_ticks INTEGER NOT NULL,
    flags INTEGER NOT NULL,
    severity INTEGER NOT NULL,
    severity_name TEXT NOT NULL,
    severity_number INTEGER NOT NULL,
    message TEXT NOT NULL,
    span_id TEXT NOT NULL,
    trace_id TEXT NOT NULL,
    parent_id TEXT NOT NULL,
    original_format TEXT NULL,
    event_name TEXT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_log_attributes (
    log_id INTEGER NOT NULL REFERENCES telemetry_logs(log_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    attribute_key TEXT NOT NULL,
    attribute_value TEXT NOT NULL,
    PRIMARY KEY (log_id, ordinal)
) STRICT;

CREATE INDEX IF NOT EXISTS ix_telemetry_logs_resource_order
    ON telemetry_logs(resource_id, timestamp_ticks, log_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_logs_order
    ON telemetry_logs(timestamp_ticks, log_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_logs_trace_span_order
    ON telemetry_logs(trace_id, span_id, timestamp_ticks, log_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_logs_trace_order
    ON telemetry_logs(trace_id, timestamp_ticks, log_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_log_attributes_owner_key
    ON telemetry_log_attributes(log_id, attribute_key);
CREATE INDEX IF NOT EXISTS ix_telemetry_log_attributes_key_value_owner
    ON telemetry_log_attributes(attribute_key COLLATE ORDINAL_IGNORE_CASE, attribute_value COLLATE ORDINAL_IGNORE_CASE, log_id);