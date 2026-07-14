-- Licensed to the .NET Foundation under one or more agreements.
-- The .NET Foundation licenses this file to you under the MIT license.

CREATE TABLE IF NOT EXISTS dashboard_resources (
    resource_name TEXT PRIMARY KEY,
    replica_index INTEGER NOT NULL,
    resource_type TEXT NOT NULL,
    display_name TEXT NOT NULL,
    uid TEXT NOT NULL,
    state TEXT NULL,
    created_at_seconds INTEGER NULL,
    created_at_nanos INTEGER NULL,
    state_style TEXT NULL,
    started_at_seconds INTEGER NULL,
    started_at_nanos INTEGER NULL,
    stopped_at_seconds INTEGER NULL,
    stopped_at_nanos INTEGER NULL,
    is_hidden INTEGER NOT NULL,
    supports_detailed_telemetry INTEGER NOT NULL,
    icon_name TEXT NULL,
    icon_variant INTEGER NULL
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_environment (
    resource_name TEXT NOT NULL REFERENCES dashboard_resources(resource_name) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    value TEXT NULL,
    is_from_spec INTEGER NOT NULL,
    PRIMARY KEY (resource_name, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_urls (
    resource_name TEXT NOT NULL REFERENCES dashboard_resources(resource_name) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    endpoint_name TEXT NULL,
    full_url TEXT NOT NULL,
    is_internal INTEGER NOT NULL,
    is_inactive INTEGER NOT NULL,
    display_sort_order INTEGER NOT NULL,
    display_name TEXT NOT NULL,
    PRIMARY KEY (resource_name, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_volumes (
    resource_name TEXT NOT NULL REFERENCES dashboard_resources(resource_name) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    source TEXT NOT NULL,
    target TEXT NOT NULL,
    mount_type TEXT NOT NULL,
    is_read_only INTEGER NOT NULL,
    PRIMARY KEY (resource_name, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_health_reports (
    resource_name TEXT NOT NULL REFERENCES dashboard_resources(resource_name) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    status INTEGER NULL,
    key TEXT NOT NULL,
    description TEXT NOT NULL,
    exception TEXT NOT NULL,
    last_run_at_seconds INTEGER NULL,
    last_run_at_nanos INTEGER NULL,
    PRIMARY KEY (resource_name, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_relationships (
    resource_name TEXT NOT NULL REFERENCES dashboard_resources(resource_name) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    related_resource_name TEXT NOT NULL,
    relationship_type TEXT NOT NULL,
    PRIMARY KEY (resource_name, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_values (
    value_id INTEGER PRIMARY KEY AUTOINCREMENT,
    resource_name TEXT NOT NULL REFERENCES dashboard_resources(resource_name) ON DELETE CASCADE,
    value_kind INTEGER NOT NULL,
    string_value TEXT NULL,
    number_value REAL NULL,
    bool_value INTEGER NULL
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_value_map_entries (
    parent_value_id INTEGER NOT NULL REFERENCES dashboard_values(value_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    map_key TEXT NOT NULL,
    child_value_id INTEGER NOT NULL REFERENCES dashboard_values(value_id) ON DELETE CASCADE,
    PRIMARY KEY (parent_value_id, ordinal),
    UNIQUE (parent_value_id, map_key)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_value_list_items (
    parent_value_id INTEGER NOT NULL REFERENCES dashboard_values(value_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    child_value_id INTEGER NOT NULL REFERENCES dashboard_values(value_id) ON DELETE CASCADE,
    PRIMARY KEY (parent_value_id, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_properties (
    resource_name TEXT NOT NULL REFERENCES dashboard_resources(resource_name) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    display_name TEXT NULL,
    value_id INTEGER NOT NULL REFERENCES dashboard_values(value_id),
    is_sensitive INTEGER NULL,
    is_highlighted INTEGER NOT NULL,
    sort_order INTEGER NULL,
    PRIMARY KEY (resource_name, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_commands (
    resource_name TEXT NOT NULL REFERENCES dashboard_resources(resource_name) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    display_name TEXT NOT NULL,
    confirmation_message TEXT NULL,
    parameter_value_id INTEGER NULL REFERENCES dashboard_values(value_id),
    is_highlighted INTEGER NOT NULL,
    icon_name TEXT NULL,
    icon_variant INTEGER NULL,
    display_description TEXT NULL,
    state INTEGER NOT NULL,
    PRIMARY KEY (resource_name, ordinal)
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_command_inputs (
    resource_name TEXT NOT NULL,
    command_ordinal INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    label TEXT NOT NULL,
    placeholder TEXT NOT NULL,
    input_type INTEGER NOT NULL,
    required INTEGER NOT NULL,
    value TEXT NOT NULL,
    description TEXT NOT NULL,
    enable_description_markdown INTEGER NOT NULL,
    max_length INTEGER NOT NULL,
    allow_custom_choice INTEGER NOT NULL,
    loading INTEGER NOT NULL,
    update_state_on_change INTEGER NOT NULL,
    name TEXT NOT NULL,
    disabled INTEGER NOT NULL,
    max_file_size INTEGER NOT NULL,
    allow_multiple_files INTEGER NOT NULL,
    file_filter TEXT NOT NULL,
    PRIMARY KEY (resource_name, command_ordinal, ordinal),
    FOREIGN KEY (resource_name, command_ordinal) REFERENCES dashboard_resource_commands(resource_name, ordinal) ON DELETE CASCADE
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_command_input_options (
    resource_name TEXT NOT NULL,
    command_ordinal INTEGER NOT NULL,
    input_ordinal INTEGER NOT NULL,
    option_key TEXT NOT NULL,
    option_value TEXT NOT NULL,
    PRIMARY KEY (resource_name, command_ordinal, input_ordinal, option_key),
    FOREIGN KEY (resource_name, command_ordinal, input_ordinal) REFERENCES dashboard_resource_command_inputs(resource_name, command_ordinal, ordinal) ON DELETE CASCADE
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_resource_command_input_validation_errors (
    resource_name TEXT NOT NULL,
    command_ordinal INTEGER NOT NULL,
    input_ordinal INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    validation_error TEXT NOT NULL,
    PRIMARY KEY (resource_name, command_ordinal, input_ordinal, ordinal),
    FOREIGN KEY (resource_name, command_ordinal, input_ordinal) REFERENCES dashboard_resource_command_inputs(resource_name, command_ordinal, ordinal) ON DELETE CASCADE
) STRICT;

CREATE TABLE IF NOT EXISTS console_logs (
    console_log_id INTEGER PRIMARY KEY AUTOINCREMENT,
    resource_name TEXT NOT NULL,
    line_number INTEGER NOT NULL,
    content TEXT NOT NULL,
    is_stderr INTEGER NOT NULL
) STRICT;

CREATE INDEX IF NOT EXISTS ix_console_logs_resource_id
ON console_logs(resource_name, console_log_id);