-- Licensed to the .NET Foundation under one or more agreements.
-- The .NET Foundation licenses this file to you under the MIT license.
-- IMPORTANT: Increment DashboardSqliteDatabase.SchemaVersion when changing the database schema.

CREATE TABLE IF NOT EXISTS dashboard_schema (
    version INTEGER NOT NULL
) STRICT;

INSERT INTO dashboard_schema (version)
SELECT @SchemaVersion
WHERE NOT EXISTS (SELECT 1 FROM dashboard_schema);