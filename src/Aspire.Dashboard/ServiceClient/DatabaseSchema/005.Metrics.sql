-- Licensed to the .NET Foundation under one or more agreements.
-- The .NET Foundation licenses this file to you under the MIT license.

CREATE TABLE IF NOT EXISTS telemetry_metric_instruments (
    instrument_id INTEGER PRIMARY KEY AUTOINCREMENT,
    resource_id INTEGER NOT NULL REFERENCES telemetry_resources(resource_id) ON DELETE CASCADE,
    scope_id INTEGER NOT NULL REFERENCES telemetry_scopes(scope_id),
    instrument_name TEXT NOT NULL,
    description TEXT NOT NULL,
    unit TEXT NOT NULL,
    instrument_type INTEGER NOT NULL,
    aggregation_temporality INTEGER NOT NULL,
    is_monotonic INTEGER NOT NULL,
    has_overflow INTEGER NOT NULL DEFAULT 0,
    UNIQUE (resource_id, scope_id, instrument_name)
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_metric_dimensions (
    dimension_id INTEGER PRIMARY KEY AUTOINCREMENT,
    instrument_id INTEGER NOT NULL REFERENCES telemetry_metric_instruments(instrument_id) ON DELETE CASCADE,
    attribute_hash INTEGER NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_metric_dimension_attributes (
    dimension_id INTEGER NOT NULL REFERENCES telemetry_metric_dimensions(dimension_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    attribute_key TEXT NOT NULL,
    attribute_value TEXT NOT NULL,
    PRIMARY KEY (dimension_id, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_metric_points (
    point_id INTEGER PRIMARY KEY AUTOINCREMENT,
    dimension_id INTEGER NOT NULL REFERENCES telemetry_metric_dimensions(dimension_id) ON DELETE CASCADE,
    point_type INTEGER NOT NULL,
    start_time_ticks INTEGER NOT NULL,
    end_time_ticks INTEGER NOT NULL,
    repeat_count INTEGER NOT NULL,
    integer_value INTEGER NULL,
    double_value REAL NULL,
    histogram_sum REAL NULL,
    histogram_count INTEGER NULL,
    bucket_counts BLOB NULL,
    explicit_bounds BLOB NULL,
    flags INTEGER NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_metric_exemplars (
    exemplar_id INTEGER PRIMARY KEY AUTOINCREMENT,
    point_id INTEGER NOT NULL REFERENCES telemetry_metric_points(point_id) ON DELETE CASCADE,
    start_time_ticks INTEGER NOT NULL,
    exemplar_value REAL NOT NULL,
    span_id TEXT NOT NULL,
    trace_id TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS telemetry_metric_exemplar_attributes (
    exemplar_id INTEGER NOT NULL REFERENCES telemetry_metric_exemplars(exemplar_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    attribute_key TEXT NOT NULL,
    attribute_value TEXT NOT NULL,
    PRIMARY KEY (exemplar_id, ordinal)
) STRICT;

CREATE INDEX IF NOT EXISTS ix_telemetry_metric_instruments_lookup
    ON telemetry_metric_instruments(resource_id, scope_id, instrument_name);
CREATE INDEX IF NOT EXISTS ix_telemetry_metric_dimensions_instrument
    ON telemetry_metric_dimensions(instrument_id, dimension_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_metric_dimensions_hash
    ON telemetry_metric_dimensions(instrument_id, attribute_hash);
CREATE INDEX IF NOT EXISTS ix_telemetry_metric_points_dimension_order
    ON telemetry_metric_points(dimension_id, point_id);
CREATE INDEX IF NOT EXISTS ix_telemetry_metric_points_time
    ON telemetry_metric_points(dimension_id, start_time_ticks, end_time_ticks);
CREATE INDEX IF NOT EXISTS ix_telemetry_metric_points_end_time
    ON telemetry_metric_points(dimension_id, end_time_ticks, start_time_ticks, point_id);
-- Exemplar identity intentionally omits span/trace IDs and filtered attributes. Distinct exemplars that share a
-- point, timestamp, and value can be collapsed, but that combination is unlikely to occur in real-world telemetry.
CREATE UNIQUE INDEX IF NOT EXISTS ix_telemetry_metric_exemplars_identity
    ON telemetry_metric_exemplars(point_id, start_time_ticks, exemplar_value);