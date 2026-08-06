// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Dapper;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.ServiceClient;

public sealed partial class SqliteResourceRepository
{
    private const int MaxWriteBatchSize = 100;

    private static void InsertResources(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<ResourceToSave> resources)
    {
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            resources,
            MaxWriteBatchSize,
            "dashboard_resources",
            [
                "resource_name", "replica_index", "resource_type", "display_name", "uid", "state",
                "created_at_seconds", "created_at_nanos", "state_style",
                "started_at_seconds", "started_at_nanos", "stopped_at_seconds", "stopped_at_nanos",
                "is_hidden", "supports_detailed_telemetry", "icon_name", "icon_variant", "console_logs_loaded"
            ],
            static (resourceToSave, parameters) =>
            {
                var resource = resourceToSave.Resource;
                parameters[0].Value = resource.Name;
                parameters[1].Value = resourceToSave.ReplicaIndex;
                parameters[2].Value = resource.ResourceType;
                parameters[3].Value = resource.DisplayName;
                parameters[4].Value = resource.Uid;
                parameters[5].Value = resource.HasState ? resource.State : DBNull.Value;
                parameters[6].Value = resource.CreatedAt?.Seconds ?? (object)DBNull.Value;
                parameters[7].Value = resource.CreatedAt?.Nanos ?? (object)DBNull.Value;
                parameters[8].Value = resource.HasStateStyle ? resource.StateStyle : DBNull.Value;
                parameters[9].Value = resource.StartedAt?.Seconds ?? (object)DBNull.Value;
                parameters[10].Value = resource.StartedAt?.Nanos ?? (object)DBNull.Value;
                parameters[11].Value = resource.StoppedAt?.Seconds ?? (object)DBNull.Value;
                parameters[12].Value = resource.StoppedAt?.Nanos ?? (object)DBNull.Value;
                parameters[13].Value = resource.IsHidden;
                parameters[14].Value = resource.SupportsDetailedTelemetry;
                parameters[15].Value = resource.HasIconName ? resource.IconName : DBNull.Value;
                parameters[16].Value = resource.HasIconVariant ? (int)resource.IconVariant : DBNull.Value;
                parameters[17].Value = resourceToSave.ConsoleLogsLoaded;
            });

        var resourceModels = resources.Select(resource => resource.Resource).ToArray();
        InsertEnvironment(connection, transaction, resourceModels);
        InsertUrls(connection, transaction, resourceModels);
        InsertVolumes(connection, transaction, resourceModels);
        InsertHealthReports(connection, transaction, resourceModels);
        InsertRelationships(connection, transaction, resourceModels);
        var properties = new List<PropertyToSave>();
        var commands = new List<CommandToSave>();
        foreach (var resource in resourceModels)
        {
            foreach (var (property, ordinal) in resource.Properties.Select((item, ordinal) => (item, ordinal)))
            {
                properties.Add(new(resource.Name, ordinal, property));
            }

#pragma warning disable CS0612 // ResourceCommand.Parameter must be persisted for compatibility with older AppHosts.
            foreach (var (command, ordinal) in resource.Commands.Select((item, ordinal) => (item, ordinal)))
            {
                var parameterJsonValue = command.Parameter is not null ? JsonFormatter.Default.Format(command.Parameter) : null;
                commands.Add(new(resource.Name, ordinal, command, parameterJsonValue));
            }
#pragma warning restore CS0612
        }

        InsertProperties(connection, transaction, properties);
        InsertCommands(connection, transaction, commands);
    }

    private static void InsertConsoleLogs(
        SqliteConnection connection,
        IDbTransaction transaction,
        string resourceName,
        IReadOnlyList<ConsoleLogToInsert> consoleLogs)
    {
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            consoleLogs,
            MaxWriteBatchSize,
            "console_logs",
            ["resource_name", "line_number", "content", "is_stderr"],
            (consoleLog, parameters) =>
            {
                parameters[0].Value = resourceName;
                parameters[1].Value = consoleLog.LineNumber;
                parameters[2].Value = consoleLog.Content;
                parameters[3].Value = consoleLog.IsStdErr;
            });
    }

    private static void InsertEnvironment(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<Resource> resources)
    {
        var rows = resources
            .SelectMany(resource => resource.Environment.Select((item, ordinal) => (ResourceName: resource.Name, Ordinal: ordinal, Item: item)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            rows,
            MaxWriteBatchSize,
            "dashboard_resource_environment",
            ["resource_name", "ordinal", "name", "value", "is_from_spec"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.ResourceName;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Item.Name;
                parameters[3].Value = row.Item.HasValue ? row.Item.Value : DBNull.Value;
                parameters[4].Value = row.Item.IsFromSpec;
            });
    }

    private static void InsertUrls(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<Resource> resources)
    {
        var rows = resources
            .SelectMany(resource => resource.Urls.Select((item, ordinal) => (ResourceName: resource.Name, Ordinal: ordinal, Item: item)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            rows,
            MaxWriteBatchSize,
            "dashboard_resource_urls",
            ["resource_name", "ordinal", "endpoint_name", "full_url", "is_internal", "is_inactive", "display_sort_order", "display_name"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.ResourceName;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Item.HasEndpointName ? row.Item.EndpointName : DBNull.Value;
                parameters[3].Value = row.Item.FullUrl;
                parameters[4].Value = row.Item.IsInternal;
                parameters[5].Value = row.Item.IsInactive;
                parameters[6].Value = row.Item.DisplayProperties.SortOrder;
                parameters[7].Value = row.Item.DisplayProperties.DisplayName;
            });
    }

    private static void InsertVolumes(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<Resource> resources)
    {
        var rows = resources
            .SelectMany(resource => resource.Volumes.Select((item, ordinal) => (ResourceName: resource.Name, Ordinal: ordinal, Item: item)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            rows,
            MaxWriteBatchSize,
            "dashboard_resource_volumes",
            ["resource_name", "ordinal", "source", "target", "mount_type", "is_read_only"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.ResourceName;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Item.Source;
                parameters[3].Value = row.Item.Target;
                parameters[4].Value = row.Item.MountType;
                parameters[5].Value = row.Item.IsReadOnly;
            });
    }

    private static void InsertHealthReports(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<Resource> resources)
    {
        var rows = resources
            .SelectMany(resource => resource.HealthReports.Select((item, ordinal) => (ResourceName: resource.Name, Ordinal: ordinal, Item: item)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            rows,
            MaxWriteBatchSize,
            "dashboard_resource_health_reports",
            ["resource_name", "ordinal", "status", "key", "description", "exception", "last_run_at_seconds", "last_run_at_nanos"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.ResourceName;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Item.HasStatus ? (int)row.Item.Status : DBNull.Value;
                parameters[3].Value = row.Item.Key;
                parameters[4].Value = row.Item.Description;
                parameters[5].Value = row.Item.Exception;
                parameters[6].Value = row.Item.LastRunAt?.Seconds ?? (object)DBNull.Value;
                parameters[7].Value = row.Item.LastRunAt?.Nanos ?? (object)DBNull.Value;
            });
    }

    private static void InsertRelationships(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<Resource> resources)
    {
        var rows = resources
            .SelectMany(resource => resource.Relationships.Select((item, ordinal) => (ResourceName: resource.Name, Ordinal: ordinal, Item: item)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            rows,
            MaxWriteBatchSize,
            "dashboard_resource_relationships",
            ["resource_name", "ordinal", "related_resource_name", "relationship_type"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.ResourceName;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Item.ResourceName;
                parameters[3].Value = row.Item.Type;
            });
    }

    private static void InsertProperties(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<PropertyToSave> properties)
    {
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            properties,
            MaxWriteBatchSize,
            "dashboard_resource_properties",
            ["resource_name", "ordinal", "name", "display_name", "value", "is_sensitive", "is_highlighted", "sort_order"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.ResourceName;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Property.Name;
                parameters[3].Value = row.Property.HasDisplayName ? row.Property.DisplayName : DBNull.Value;
                parameters[4].Value = JsonFormatter.Default.Format(row.Property.Value);
                parameters[5].Value = row.Property.HasIsSensitive ? row.Property.IsSensitive : DBNull.Value;
                parameters[6].Value = row.Property.IsHighlighted;
                parameters[7].Value = row.Property.HasSortOrder ? row.Property.SortOrder : DBNull.Value;
            });
    }

    private static void InsertCommands(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<CommandToSave> commands)
    {
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            commands,
            MaxWriteBatchSize,
            "dashboard_resource_commands",
            [
                "resource_name", "ordinal", "name", "display_name", "confirmation_message", "parameter_value",
                "is_highlighted", "icon_name", "icon_variant", "display_description", "state"
            ],
            static (row, parameters) =>
            {
                var command = row.Command;
                parameters[0].Value = row.ResourceName;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = command.Name;
                parameters[3].Value = command.DisplayName;
                parameters[4].Value = command.HasConfirmationMessage ? command.ConfirmationMessage : DBNull.Value;
                parameters[5].Value = row.ParameterJsonValue ?? (object)DBNull.Value;
                parameters[6].Value = command.IsHighlighted;
                parameters[7].Value = command.HasIconName ? command.IconName : DBNull.Value;
                parameters[8].Value = command.HasIconVariant ? (int)command.IconVariant : DBNull.Value;
                parameters[9].Value = command.HasDisplayDescription ? command.DisplayDescription : DBNull.Value;
                parameters[10].Value = (int)command.State;
            });

        var inputs = commands.SelectMany(command => command.Command.ArgumentInputs.Select((input, ordinal) => (Command: command, Ordinal: ordinal, Input: input))).ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            inputs,
            MaxWriteBatchSize,
            "dashboard_resource_command_inputs",
            [
                "resource_name", "command_ordinal", "ordinal", "label", "placeholder", "input_type", "required", "value",
                "description", "enable_description_markdown", "max_length", "allow_custom_choice", "loading",
                "update_state_on_change", "name", "disabled", "max_file_size", "allow_multiple_files", "file_filter"
            ],
            static (row, parameters) =>
            {
                var input = row.Input;
                parameters[0].Value = row.Command.ResourceName;
                parameters[1].Value = row.Command.Ordinal;
                parameters[2].Value = row.Ordinal;
                parameters[3].Value = input.Label;
                parameters[4].Value = input.Placeholder;
                parameters[5].Value = (int)input.InputType;
                parameters[6].Value = input.Required;
                parameters[7].Value = input.Value;
                parameters[8].Value = input.Description;
                parameters[9].Value = input.EnableDescriptionMarkdown;
                parameters[10].Value = input.MaxLength;
                parameters[11].Value = input.AllowCustomChoice;
                parameters[12].Value = input.Loading;
                parameters[13].Value = input.UpdateStateOnChange;
                parameters[14].Value = input.Name;
                parameters[15].Value = input.Disabled;
                parameters[16].Value = input.MaxFileSize;
                parameters[17].Value = input.AllowMultipleFiles;
                parameters[18].Value = input.FileFilter;
            });

        var options = inputs
            .SelectMany(row => row.Input.Options.Select(option => (row.Command, InputOrdinal: row.Ordinal, Option: option)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            options,
            MaxWriteBatchSize,
            "dashboard_resource_command_input_options",
            ["resource_name", "command_ordinal", "input_ordinal", "option_key", "option_value"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.Command.ResourceName;
                parameters[1].Value = row.Command.Ordinal;
                parameters[2].Value = row.InputOrdinal;
                parameters[3].Value = row.Option.Key;
                parameters[4].Value = row.Option.Value;
            });

        var validationErrors = inputs
            .SelectMany(row => row.Input.ValidationErrors.Select((validationError, ordinal) => (row.Command, InputOrdinal: row.Ordinal, Ordinal: ordinal, ValidationError: validationError)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            validationErrors,
            MaxWriteBatchSize,
            "dashboard_resource_command_input_validation_errors",
            ["resource_name", "command_ordinal", "input_ordinal", "ordinal", "validation_error"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.Command.ResourceName;
                parameters[1].Value = row.Command.Ordinal;
                parameters[2].Value = row.InputOrdinal;
                parameters[3].Value = row.Ordinal;
                parameters[4].Value = row.ValidationError;
            });
    }

    private static IEnumerable<StoredResource> LoadResourceRecords(SqliteConnection connection)
    {
        using var reader = connection.QueryMultiple("""
            SELECT
                resource_name AS ResourceName,
                replica_index AS ReplicaIndex,
                resource_type AS ResourceType,
                display_name AS DisplayName,
                uid AS Uid,
                state AS State,
                created_at_seconds AS CreatedAtSeconds,
                created_at_nanos AS CreatedAtNanos,
                state_style AS StateStyle,
                started_at_seconds AS StartedAtSeconds,
                started_at_nanos AS StartedAtNanos,
                stopped_at_seconds AS StoppedAtSeconds,
                stopped_at_nanos AS StoppedAtNanos,
                is_hidden AS IsHidden,
                supports_detailed_telemetry AS SupportsDetailedTelemetry,
                icon_name AS IconName,
                icon_variant AS IconVariant,
                console_logs_loaded AS ConsoleLogsLoaded
            FROM dashboard_resources
            ORDER BY rowid;

            SELECT resource_name AS ResourceName, name AS Name, value AS Value, is_from_spec AS IsFromSpec
            FROM dashboard_resource_environment
            ORDER BY resource_name, ordinal;

            SELECT resource_name AS ResourceName, endpoint_name AS EndpointName, full_url AS FullUrl,
                is_internal AS IsInternal, is_inactive AS IsInactive, display_sort_order AS DisplaySortOrder, display_name AS DisplayName
            FROM dashboard_resource_urls
            ORDER BY resource_name, ordinal;

            SELECT resource_name AS ResourceName, source AS Source, target AS Target, mount_type AS MountType, is_read_only AS IsReadOnly
            FROM dashboard_resource_volumes
            ORDER BY resource_name, ordinal;

            SELECT resource_name AS ResourceName, status AS Status, key AS Key, description AS Description,
                exception AS Exception, last_run_at_seconds AS LastRunAtSeconds, last_run_at_nanos AS LastRunAtNanos
            FROM dashboard_resource_health_reports
            ORDER BY resource_name, ordinal;

            SELECT resource_name AS ResourceName, related_resource_name AS RelatedResourceName, relationship_type AS RelationshipType
            FROM dashboard_resource_relationships
            ORDER BY resource_name, ordinal;

            SELECT resource_name AS ResourceName, name AS Name, display_name AS DisplayName, value AS JsonValue,
                is_sensitive AS IsSensitive, is_highlighted AS IsHighlighted, sort_order AS SortOrder
            FROM dashboard_resource_properties
            ORDER BY resource_name, ordinal;

            SELECT resource_name AS ResourceName, ordinal AS Ordinal, name AS Name, display_name AS DisplayName,
                confirmation_message AS ConfirmationMessage, parameter_value AS ParameterJsonValue,
                is_highlighted AS IsHighlighted, icon_name AS IconName, icon_variant AS IconVariant,
                display_description AS DisplayDescription, state AS State
            FROM dashboard_resource_commands
            ORDER BY resource_name, ordinal;

            SELECT resource_name AS ResourceName, command_ordinal AS CommandOrdinal, ordinal AS Ordinal,
                label AS Label, placeholder AS Placeholder, input_type AS InputType, required AS Required,
                value AS Value, description AS Description, enable_description_markdown AS EnableDescriptionMarkdown,
                max_length AS MaxLength, allow_custom_choice AS AllowCustomChoice, loading AS Loading,
                update_state_on_change AS UpdateStateOnChange, name AS Name, disabled AS Disabled,
                max_file_size AS MaxFileSize, allow_multiple_files AS AllowMultipleFiles, file_filter AS FileFilter
            FROM dashboard_resource_command_inputs
            ORDER BY resource_name, command_ordinal, ordinal;

            SELECT resource_name AS ResourceName, command_ordinal AS CommandOrdinal, input_ordinal AS InputOrdinal,
                option_key AS OptionKey, option_value AS OptionValue
            FROM dashboard_resource_command_input_options
            ORDER BY resource_name, command_ordinal, input_ordinal, option_key;

            SELECT resource_name AS ResourceName, command_ordinal AS CommandOrdinal, input_ordinal AS InputOrdinal,
                validation_error AS ValidationError
            FROM dashboard_resource_command_input_validation_errors
            ORDER BY resource_name, command_ordinal, input_ordinal, ordinal;

            """);

        var resourceRecords = reader.Read<ResourceRecord>().AsList();
        var environments = reader.Read<EnvironmentRecord>().ToLookup(record => record.ResourceName, StringComparers.ResourceName);
        var urls = reader.Read<UrlRecord>().ToLookup(record => record.ResourceName, StringComparers.ResourceName);
        var volumes = reader.Read<VolumeRecord>().ToLookup(record => record.ResourceName, StringComparers.ResourceName);
        var healthReports = reader.Read<HealthReportRecord>().ToLookup(record => record.ResourceName, StringComparers.ResourceName);
        var relationships = reader.Read<RelationshipRecord>().ToLookup(record => record.ResourceName, StringComparers.ResourceName);
        var properties = reader.Read<PropertyRecord>().ToLookup(record => record.ResourceName, StringComparers.ResourceName);
        var commands = reader.Read<CommandRecord>().ToLookup(record => record.ResourceName, StringComparers.ResourceName);
        var inputs = reader.Read<InputRecord>().ToLookup(record => (record.ResourceName, record.CommandOrdinal));
        var options = reader.Read<OptionRecord>().ToLookup(record => (record.ResourceName, record.CommandOrdinal, record.InputOrdinal));
        var validationErrors = reader.Read<ValidationErrorRecord>().ToLookup(record => (record.ResourceName, record.CommandOrdinal, record.InputOrdinal));

        foreach (var record in resourceRecords)
        {
            var resource = new Resource
            {
                Name = record.ResourceName,
                ResourceType = record.ResourceType,
                DisplayName = record.DisplayName,
                Uid = record.Uid,
                IsHidden = record.IsHidden,
                SupportsDetailedTelemetry = record.SupportsDetailedTelemetry
            };

            SetOptionalResourceFields(resource, record);
            foreach (var environment in environments[record.ResourceName])
            {
                var item = new EnvironmentVariable { Name = environment.Name, IsFromSpec = environment.IsFromSpec };
                if (environment.Value is not null)
                {
                    item.Value = environment.Value;
                }
                resource.Environment.Add(item);
            }
            foreach (var url in urls[record.ResourceName])
            {
                var item = new Url
                {
                    FullUrl = url.FullUrl,
                    IsInternal = url.IsInternal,
                    IsInactive = url.IsInactive,
                    DisplayProperties = new UrlDisplayProperties { SortOrder = url.DisplaySortOrder, DisplayName = url.DisplayName }
                };
                if (url.EndpointName is not null)
                {
                    item.EndpointName = url.EndpointName;
                }
                resource.Urls.Add(item);
            }
            resource.Volumes.Add(volumes[record.ResourceName].Select(volume => new Volume
            {
                Source = volume.Source,
                Target = volume.Target,
                MountType = volume.MountType,
                IsReadOnly = volume.IsReadOnly
            }));
            foreach (var healthReport in healthReports[record.ResourceName])
            {
                var item = new HealthReport { Key = healthReport.Key, Description = healthReport.Description, Exception = healthReport.Exception };
                if (healthReport.Status is not null)
                {
                    item.Status = (HealthStatus)healthReport.Status.Value;
                }
                if (healthReport.LastRunAtSeconds is not null)
                {
                    item.LastRunAt = CreateTimestamp(healthReport.LastRunAtSeconds.Value, healthReport.LastRunAtNanos);
                }
                resource.HealthReports.Add(item);
            }
            resource.Relationships.Add(relationships[record.ResourceName].Select(relationship => new ResourceRelationship
            {
                ResourceName = relationship.RelatedResourceName,
                Type = relationship.RelationshipType
            }));
            foreach (var property in properties[record.ResourceName])
            {
                var item = new ResourceProperty
                {
                    Name = property.Name,
                    Value = MaterializeValue(property.JsonValue),
                    IsHighlighted = property.IsHighlighted
                };
                if (property.DisplayName is not null)
                {
                    item.DisplayName = property.DisplayName;
                }
                if (property.IsSensitive is not null)
                {
                    item.IsSensitive = property.IsSensitive.Value;
                }
                if (property.SortOrder is not null)
                {
                    item.SortOrder = property.SortOrder.Value;
                }
                resource.Properties.Add(item);
            }

#pragma warning disable CS0612 // ResourceCommand.Parameter must be restored for compatibility with older AppHosts.
            foreach (var commandRecord in commands[record.ResourceName])
            {
                var command = new ResourceCommand
                {
                    Name = commandRecord.Name,
                    DisplayName = commandRecord.DisplayName,
                    IsHighlighted = commandRecord.IsHighlighted,
                    State = (ResourceCommandState)commandRecord.State
                };
                if (commandRecord.ConfirmationMessage is not null)
                {
                    command.ConfirmationMessage = commandRecord.ConfirmationMessage;
                }
                if (commandRecord.ParameterJsonValue is not null)
                {
                    command.Parameter = MaterializeValue(commandRecord.ParameterJsonValue);
                }
                if (commandRecord.IconName is not null)
                {
                    command.IconName = commandRecord.IconName;
                }
                if (commandRecord.IconVariant is not null)
                {
                    command.IconVariant = (IconVariant)commandRecord.IconVariant.Value;
                }
                if (commandRecord.DisplayDescription is not null)
                {
                    command.DisplayDescription = commandRecord.DisplayDescription;
                }

                foreach (var inputRecord in inputs[(record.ResourceName, commandRecord.Ordinal)])
                {
                    var input = new InteractionInput
                    {
                        Label = inputRecord.Label,
                        Placeholder = inputRecord.Placeholder,
                        InputType = (InputType)inputRecord.InputType,
                        Required = inputRecord.Required,
                        Value = inputRecord.Value,
                        Description = inputRecord.Description,
                        EnableDescriptionMarkdown = inputRecord.EnableDescriptionMarkdown,
                        MaxLength = inputRecord.MaxLength,
                        AllowCustomChoice = inputRecord.AllowCustomChoice,
                        Loading = inputRecord.Loading,
                        UpdateStateOnChange = inputRecord.UpdateStateOnChange,
                        Name = inputRecord.Name,
                        Disabled = inputRecord.Disabled,
                        MaxFileSize = inputRecord.MaxFileSize,
                        AllowMultipleFiles = inputRecord.AllowMultipleFiles,
                        FileFilter = inputRecord.FileFilter
                    };
                    foreach (var option in options[(record.ResourceName, commandRecord.Ordinal, inputRecord.Ordinal)])
                    {
                        input.Options.Add(option.OptionKey, option.OptionValue);
                    }
                    input.ValidationErrors.Add(validationErrors[(record.ResourceName, commandRecord.Ordinal, inputRecord.Ordinal)].Select(error => error.ValidationError));
                    command.ArgumentInputs.Add(input);
                }
                resource.Commands.Add(command);
            }
#pragma warning restore CS0612

            yield return new StoredResource(resource, record.ReplicaIndex, record.ConsoleLogsLoaded);
        }
    }

    private static Value MaterializeValue(string jsonValue) => JsonParser.Default.Parse<Value>(jsonValue);

    private static void SetOptionalResourceFields(Resource resource, ResourceRecord record)
    {
        if (record.State is not null)
        {
            resource.State = record.State;
        }
        if (record.CreatedAtSeconds is not null)
        {
            resource.CreatedAt = CreateTimestamp(record.CreatedAtSeconds.Value, record.CreatedAtNanos);
        }
        if (record.StateStyle is not null)
        {
            resource.StateStyle = record.StateStyle;
        }
        if (record.StartedAtSeconds is not null)
        {
            resource.StartedAt = CreateTimestamp(record.StartedAtSeconds.Value, record.StartedAtNanos);
        }
        if (record.StoppedAtSeconds is not null)
        {
            resource.StoppedAt = CreateTimestamp(record.StoppedAtSeconds.Value, record.StoppedAtNanos);
        }
        if (record.IconName is not null)
        {
            resource.IconName = record.IconName;
        }
        if (record.IconVariant is not null)
        {
            resource.IconVariant = (IconVariant)record.IconVariant.Value;
        }
    }

    private static Timestamp CreateTimestamp(long seconds, int? nanos)
    {
        return new Timestamp { Seconds = seconds, Nanos = nanos ?? 0 };
    }

    private sealed record ResourceToSave(Resource Resource, int ReplicaIndex, bool ConsoleLogsLoaded);

    private sealed record ConsoleLogToInsert(int LineNumber, string Content, bool IsStdErr);

    private sealed record PropertyToSave(string ResourceName, int Ordinal, ResourceProperty Property);

    private sealed record CommandToSave(string ResourceName, int Ordinal, ResourceCommand Command, string? ParameterJsonValue);

    private sealed record StoredResource(Resource Resource, int ReplicaIndex, bool ConsoleLogsLoaded);

    private sealed class ResourceRecord
    {
        public required string ResourceName { get; init; }
        public required int ReplicaIndex { get; init; }
        public required string ResourceType { get; init; }
        public required string DisplayName { get; init; }
        public required string Uid { get; init; }
        public string? State { get; init; }
        public long? CreatedAtSeconds { get; init; }
        public int? CreatedAtNanos { get; init; }
        public string? StateStyle { get; init; }
        public long? StartedAtSeconds { get; init; }
        public int? StartedAtNanos { get; init; }
        public long? StoppedAtSeconds { get; init; }
        public int? StoppedAtNanos { get; init; }
        public required bool IsHidden { get; init; }
        public required bool SupportsDetailedTelemetry { get; init; }
        public string? IconName { get; init; }
        public int? IconVariant { get; init; }
        public required bool ConsoleLogsLoaded { get; init; }
    }

    private sealed class EnvironmentRecord
    {
        public required string ResourceName { get; init; }
        public required string Name { get; init; }
        public string? Value { get; init; }
        public required bool IsFromSpec { get; init; }
    }

    private sealed class UrlRecord
    {
        public required string ResourceName { get; init; }
        public string? EndpointName { get; init; }
        public required string FullUrl { get; init; }
        public required bool IsInternal { get; init; }
        public required bool IsInactive { get; init; }
        public required int DisplaySortOrder { get; init; }
        public required string DisplayName { get; init; }
    }

    private sealed class VolumeRecord
    {
        public required string ResourceName { get; init; }
        public required string Source { get; init; }
        public required string Target { get; init; }
        public required string MountType { get; init; }
        public required bool IsReadOnly { get; init; }
    }

    private sealed class HealthReportRecord
    {
        public required string ResourceName { get; init; }
        public int? Status { get; init; }
        public required string Key { get; init; }
        public required string Description { get; init; }
        public required string Exception { get; init; }
        public long? LastRunAtSeconds { get; init; }
        public int? LastRunAtNanos { get; init; }
    }

    private sealed class PropertyRecord
    {
        public required string ResourceName { get; init; }
        public required string Name { get; init; }
        public string? DisplayName { get; init; }
        public required string JsonValue { get; init; }
        public bool? IsSensitive { get; init; }
        public required bool IsHighlighted { get; init; }
        public int? SortOrder { get; init; }
    }

    private sealed class CommandRecord
    {
        public required string ResourceName { get; init; }
        public required int Ordinal { get; init; }
        public required string Name { get; init; }
        public required string DisplayName { get; init; }
        public string? ConfirmationMessage { get; init; }
        public string? ParameterJsonValue { get; init; }
        public required bool IsHighlighted { get; init; }
        public string? IconName { get; init; }
        public int? IconVariant { get; init; }
        public string? DisplayDescription { get; init; }
        public required int State { get; init; }
    }

    private sealed class InputRecord
    {
        public required string ResourceName { get; init; }
        public required int CommandOrdinal { get; init; }
        public required int Ordinal { get; init; }
        public required string Label { get; init; }
        public required string Placeholder { get; init; }
        public required int InputType { get; init; }
        public required bool Required { get; init; }
        public required string Value { get; init; }
        public required string Description { get; init; }
        public required bool EnableDescriptionMarkdown { get; init; }
        public required int MaxLength { get; init; }
        public required bool AllowCustomChoice { get; init; }
        public required bool Loading { get; init; }
        public required bool UpdateStateOnChange { get; init; }
        public required string Name { get; init; }
        public required bool Disabled { get; init; }
        public required long MaxFileSize { get; init; }
        public required bool AllowMultipleFiles { get; init; }
        public required string FileFilter { get; init; }
    }

    private sealed class OptionRecord
    {
        public required string ResourceName { get; init; }
        public required int CommandOrdinal { get; init; }
        public required int InputOrdinal { get; init; }
        public required string OptionKey { get; init; }
        public required string OptionValue { get; init; }
    }

    private sealed class RelationshipRecord
    {
        public required string ResourceName { get; init; }
        public required string RelatedResourceName { get; init; }
        public required string RelationshipType { get; init; }
    }

    private sealed class ValidationErrorRecord
    {
        public required string ResourceName { get; init; }
        public required int CommandOrdinal { get; init; }
        public required int InputOrdinal { get; init; }
        public required string ValidationError { get; init; }
    }

}