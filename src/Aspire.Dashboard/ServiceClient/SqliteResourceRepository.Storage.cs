// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using Aspire.DashboardService.Proto.V1;
using Dapper;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.ServiceClient;

public sealed partial class SqliteResourceRepository
{
    private static void SaveResource(SqliteConnection connection, IDbTransaction transaction, Resource resource, int replicaIndex)
    {
        connection.Execute("DELETE FROM dashboard_resources WHERE resource_name = @ResourceName;", new { ResourceName = resource.Name }, transaction);
        connection.Execute("""
            INSERT INTO dashboard_resources (
                resource_name, replica_index, resource_type, display_name, uid, state,
                created_at_seconds, created_at_nanos, state_style,
                started_at_seconds, started_at_nanos, stopped_at_seconds, stopped_at_nanos,
                is_hidden, supports_detailed_telemetry, icon_name, icon_variant)
            VALUES (
                @Name, @ReplicaIndex, @ResourceType, @DisplayName, @Uid, @State,
                @CreatedAtSeconds, @CreatedAtNanos, @StateStyle,
                @StartedAtSeconds, @StartedAtNanos, @StoppedAtSeconds, @StoppedAtNanos,
                @IsHidden, @SupportsDetailedTelemetry, @IconName, @IconVariant);
            """, new
        {
            resource.Name,
            ReplicaIndex = replicaIndex,
            resource.ResourceType,
            resource.DisplayName,
            resource.Uid,
            State = resource.HasState ? resource.State : null,
            CreatedAtSeconds = resource.CreatedAt?.Seconds,
            CreatedAtNanos = resource.CreatedAt?.Nanos,
            StateStyle = resource.HasStateStyle ? resource.StateStyle : null,
            StartedAtSeconds = resource.StartedAt?.Seconds,
            StartedAtNanos = resource.StartedAt?.Nanos,
            StoppedAtSeconds = resource.StoppedAt?.Seconds,
            StoppedAtNanos = resource.StoppedAt?.Nanos,
            resource.IsHidden,
            resource.SupportsDetailedTelemetry,
            IconName = resource.HasIconName ? resource.IconName : null,
            IconVariant = resource.HasIconVariant ? (int?)resource.IconVariant : null
        }, transaction);

        InsertEnvironment(connection, transaction, resource);
        InsertUrls(connection, transaction, resource);
        InsertVolumes(connection, transaction, resource);
        InsertHealthReports(connection, transaction, resource);
        InsertRelationships(connection, transaction, resource);
        InsertProperties(connection, transaction, resource);
        InsertCommands(connection, transaction, resource);
    }

    private static void InsertEnvironment(SqliteConnection connection, IDbTransaction transaction, Resource resource)
    {
        connection.Execute("""
            INSERT INTO dashboard_resource_environment (resource_name, ordinal, name, value, is_from_spec)
            VALUES (@ResourceName, @Ordinal, @Name, @Value, @IsFromSpec);
            """, resource.Environment.Select((item, ordinal) => new
        {
            ResourceName = resource.Name,
            Ordinal = ordinal,
            item.Name,
            Value = item.HasValue ? item.Value : null,
            item.IsFromSpec
        }), transaction);
    }

    private static void InsertUrls(SqliteConnection connection, IDbTransaction transaction, Resource resource)
    {
        connection.Execute("""
            INSERT INTO dashboard_resource_urls (
                resource_name, ordinal, endpoint_name, full_url, is_internal, is_inactive, display_sort_order, display_name)
            VALUES (
                @ResourceName, @Ordinal, @EndpointName, @FullUrl, @IsInternal, @IsInactive, @DisplaySortOrder, @DisplayName);
            """, resource.Urls.Select((item, ordinal) => new
        {
            ResourceName = resource.Name,
            Ordinal = ordinal,
            EndpointName = item.HasEndpointName ? item.EndpointName : null,
            item.FullUrl,
            item.IsInternal,
            item.IsInactive,
            DisplaySortOrder = item.DisplayProperties.SortOrder,
            DisplayName = item.DisplayProperties.DisplayName
        }), transaction);
    }

    private static void InsertVolumes(SqliteConnection connection, IDbTransaction transaction, Resource resource)
    {
        connection.Execute("""
            INSERT INTO dashboard_resource_volumes (resource_name, ordinal, source, target, mount_type, is_read_only)
            VALUES (@ResourceName, @Ordinal, @Source, @Target, @MountType, @IsReadOnly);
            """, resource.Volumes.Select((item, ordinal) => new
        {
            ResourceName = resource.Name,
            Ordinal = ordinal,
            item.Source,
            item.Target,
            item.MountType,
            item.IsReadOnly
        }), transaction);
    }

    private static void InsertHealthReports(SqliteConnection connection, IDbTransaction transaction, Resource resource)
    {
        connection.Execute("""
            INSERT INTO dashboard_resource_health_reports (
                resource_name, ordinal, status, key, description, exception, last_run_at_seconds, last_run_at_nanos)
            VALUES (
                @ResourceName, @Ordinal, @Status, @Key, @Description, @Exception, @LastRunAtSeconds, @LastRunAtNanos);
            """, resource.HealthReports.Select((item, ordinal) => new
        {
            ResourceName = resource.Name,
            Ordinal = ordinal,
            Status = item.HasStatus ? (int?)item.Status : null,
            item.Key,
            item.Description,
            item.Exception,
            LastRunAtSeconds = item.LastRunAt?.Seconds,
            LastRunAtNanos = item.LastRunAt?.Nanos
        }), transaction);
    }

    private static void InsertRelationships(SqliteConnection connection, IDbTransaction transaction, Resource resource)
    {
        connection.Execute("""
            INSERT INTO dashboard_resource_relationships (
                resource_name, ordinal, related_resource_name, relationship_type)
            VALUES (@ResourceName, @Ordinal, @RelatedResourceName, @RelationshipType);
            """, resource.Relationships.Select((item, ordinal) => new
        {
            ResourceName = resource.Name,
            Ordinal = ordinal,
            RelatedResourceName = item.ResourceName,
            RelationshipType = item.Type
        }), transaction);
    }

    private static void InsertProperties(SqliteConnection connection, IDbTransaction transaction, Resource resource)
    {
        foreach (var (property, ordinal) in resource.Properties.Select((item, ordinal) => (item, ordinal)))
        {
            var valueId = InsertValue(connection, transaction, resource.Name, property.Value);
            connection.Execute("""
                INSERT INTO dashboard_resource_properties (
                    resource_name, ordinal, name, display_name, value_id, is_sensitive, is_highlighted, sort_order)
                VALUES (
                    @ResourceName, @Ordinal, @Name, @DisplayName, @ValueId, @IsSensitive, @IsHighlighted, @SortOrder);
                """, new
            {
                ResourceName = resource.Name,
                Ordinal = ordinal,
                property.Name,
                DisplayName = property.HasDisplayName ? property.DisplayName : null,
                ValueId = valueId,
                IsSensitive = property.HasIsSensitive ? (bool?)property.IsSensitive : null,
                property.IsHighlighted,
                SortOrder = property.HasSortOrder ? (int?)property.SortOrder : null
            }, transaction);
        }
    }

    private static void InsertCommands(SqliteConnection connection, IDbTransaction transaction, Resource resource)
    {
#pragma warning disable CS0612 // ResourceCommand.Parameter must be persisted for compatibility with older AppHosts.
        foreach (var (command, commandOrdinal) in resource.Commands.Select((item, ordinal) => (item, ordinal)))
        {
            long? parameterValueId = command.Parameter is not null
                ? InsertValue(connection, transaction, resource.Name, command.Parameter)
                : null;
            connection.Execute("""
                INSERT INTO dashboard_resource_commands (
                    resource_name, ordinal, name, display_name, confirmation_message, parameter_value_id,
                    is_highlighted, icon_name, icon_variant, display_description, state)
                VALUES (
                    @ResourceName, @Ordinal, @Name, @DisplayName, @ConfirmationMessage, @ParameterValueId,
                    @IsHighlighted, @IconName, @IconVariant, @DisplayDescription, @State);
                """, new
            {
                ResourceName = resource.Name,
                Ordinal = commandOrdinal,
                command.Name,
                command.DisplayName,
                ConfirmationMessage = command.HasConfirmationMessage ? command.ConfirmationMessage : null,
                ParameterValueId = parameterValueId,
                command.IsHighlighted,
                IconName = command.HasIconName ? command.IconName : null,
                IconVariant = command.HasIconVariant ? (int?)command.IconVariant : null,
                DisplayDescription = command.HasDisplayDescription ? command.DisplayDescription : null,
                State = (int)command.State
            }, transaction);

            foreach (var (input, inputOrdinal) in command.ArgumentInputs.Select((item, ordinal) => (item, ordinal)))
            {
                connection.Execute("""
                    INSERT INTO dashboard_resource_command_inputs (
                        resource_name, command_ordinal, ordinal, label, placeholder, input_type, required, value,
                        description, enable_description_markdown, max_length, allow_custom_choice, loading,
                        update_state_on_change, name, disabled, max_file_size, allow_multiple_files, file_filter)
                    VALUES (
                        @ResourceName, @CommandOrdinal, @Ordinal, @Label, @Placeholder, @InputType, @Required, @Value,
                        @Description, @EnableDescriptionMarkdown, @MaxLength, @AllowCustomChoice, @Loading,
                        @UpdateStateOnChange, @Name, @Disabled, @MaxFileSize, @AllowMultipleFiles, @FileFilter);
                    """, new
                {
                    ResourceName = resource.Name,
                    CommandOrdinal = commandOrdinal,
                    Ordinal = inputOrdinal,
                    input.Label,
                    input.Placeholder,
                    InputType = (int)input.InputType,
                    input.Required,
                    input.Value,
                    input.Description,
                    input.EnableDescriptionMarkdown,
                    input.MaxLength,
                    input.AllowCustomChoice,
                    input.Loading,
                    input.UpdateStateOnChange,
                    input.Name,
                    input.Disabled,
                    input.MaxFileSize,
                    input.AllowMultipleFiles,
                    input.FileFilter
                }, transaction);

                connection.Execute("""
                    INSERT INTO dashboard_resource_command_input_options (
                        resource_name, command_ordinal, input_ordinal, option_key, option_value)
                    VALUES (@ResourceName, @CommandOrdinal, @InputOrdinal, @OptionKey, @OptionValue);
                    """, input.Options.Select(option => new
                {
                    ResourceName = resource.Name,
                    CommandOrdinal = commandOrdinal,
                    InputOrdinal = inputOrdinal,
                    OptionKey = option.Key,
                    OptionValue = option.Value
                }), transaction);

                connection.Execute("""
                    INSERT INTO dashboard_resource_command_input_validation_errors (
                        resource_name, command_ordinal, input_ordinal, ordinal, validation_error)
                    VALUES (@ResourceName, @CommandOrdinal, @InputOrdinal, @Ordinal, @ValidationError);
                    """, input.ValidationErrors.Select((validationError, ordinal) => new
                {
                    ResourceName = resource.Name,
                    CommandOrdinal = commandOrdinal,
                    InputOrdinal = inputOrdinal,
                    Ordinal = ordinal,
                    ValidationError = validationError
                }), transaction);
            }
        }
#pragma warning restore CS0612
    }

    private static long InsertValue(SqliteConnection connection, IDbTransaction transaction, string resourceName, Value value)
    {
        var valueId = connection.QuerySingle<long>("""
            INSERT INTO dashboard_values (resource_name, value_kind, string_value, number_value, bool_value)
            VALUES (@ResourceName, @ValueKind, @StringValue, @NumberValue, @BoolValue)
            RETURNING value_id;
            """, new
        {
            ResourceName = resourceName,
            ValueKind = (int)value.KindCase,
            StringValue = value.KindCase == Value.KindOneofCase.StringValue ? value.StringValue : null,
            NumberValue = value.KindCase == Value.KindOneofCase.NumberValue ? (double?)value.NumberValue : null,
            BoolValue = value.KindCase == Value.KindOneofCase.BoolValue ? (bool?)value.BoolValue : null
        }, transaction);

        if (value.KindCase == Value.KindOneofCase.StructValue)
        {
            var ordinal = 0;
            foreach (var field in value.StructValue.Fields)
            {
                var childValueId = InsertValue(connection, transaction, resourceName, field.Value);
                connection.Execute("""
                    INSERT INTO dashboard_value_map_entries (parent_value_id, ordinal, map_key, child_value_id)
                    VALUES (@ParentValueId, @Ordinal, @MapKey, @ChildValueId);
                    """, new { ParentValueId = valueId, Ordinal = ordinal++, MapKey = field.Key, ChildValueId = childValueId }, transaction);
            }
        }
        else if (value.KindCase == Value.KindOneofCase.ListValue)
        {
            foreach (var (item, ordinal) in value.ListValue.Values.Select((item, ordinal) => (item, ordinal)))
            {
                var childValueId = InsertValue(connection, transaction, resourceName, item);
                connection.Execute("""
                    INSERT INTO dashboard_value_list_items (parent_value_id, ordinal, child_value_id)
                    VALUES (@ParentValueId, @Ordinal, @ChildValueId);
                    """, new { ParentValueId = valueId, Ordinal = ordinal, ChildValueId = childValueId }, transaction);
            }
        }

        return valueId;
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
                icon_variant AS IconVariant
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

            SELECT resource_name AS ResourceName, name AS Name, display_name AS DisplayName, value_id AS ValueId,
                is_sensitive AS IsSensitive, is_highlighted AS IsHighlighted, sort_order AS SortOrder
            FROM dashboard_resource_properties
            ORDER BY resource_name, ordinal;

            SELECT resource_name AS ResourceName, ordinal AS Ordinal, name AS Name, display_name AS DisplayName,
                confirmation_message AS ConfirmationMessage, parameter_value_id AS ParameterValueId,
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

            SELECT value_id AS ValueId, value_kind AS ValueKind, string_value AS StringValue,
                number_value AS NumberValue, bool_value AS BoolValue
            FROM dashboard_values;

            SELECT parent_value_id AS ParentValueId, map_key AS MapKey, child_value_id AS ChildValueId
            FROM dashboard_value_map_entries
            ORDER BY parent_value_id, ordinal;

            SELECT parent_value_id AS ParentValueId, child_value_id AS ChildValueId
            FROM dashboard_value_list_items
            ORDER BY parent_value_id, ordinal;
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
        var values = reader.Read<ValueRecord>().ToDictionary(record => record.ValueId);
        var mapValues = reader.Read<MapValueRecord>().ToLookup(record => record.ParentValueId);
        var listValues = reader.Read<ListValueRecord>().ToLookup(record => record.ParentValueId);

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
                    Value = MaterializeValue(property.ValueId, values, mapValues, listValues),
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
                if (commandRecord.ParameterValueId is not null)
                {
                    command.Parameter = MaterializeValue(commandRecord.ParameterValueId.Value, values, mapValues, listValues);
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

            yield return new StoredResource(resource, record.ReplicaIndex);
        }
    }

    private static Value MaterializeValue(
        long valueId,
        IReadOnlyDictionary<long, ValueRecord> values,
        ILookup<long, MapValueRecord> mapValues,
        ILookup<long, ListValueRecord> listValues)
    {
        var record = values[valueId];
        var value = new Value();
        switch ((Value.KindOneofCase)record.ValueKind)
        {
            case Value.KindOneofCase.NullValue:
                value.NullValue = NullValue.NullValue;
                break;
            case Value.KindOneofCase.NumberValue:
                value.NumberValue = record.NumberValue!.Value;
                break;
            case Value.KindOneofCase.StringValue:
                value.StringValue = record.StringValue!;
                break;
            case Value.KindOneofCase.BoolValue:
                value.BoolValue = record.BoolValue!.Value;
                break;
            case Value.KindOneofCase.StructValue:
                value.StructValue = new Struct();
                foreach (var child in mapValues[valueId])
                {
                    value.StructValue.Fields.Add(child.MapKey, MaterializeValue(child.ChildValueId, values, mapValues, listValues));
                }
                break;
            case Value.KindOneofCase.ListValue:
                value.ListValue = new ListValue();
                value.ListValue.Values.Add(listValues[valueId].Select(child => MaterializeValue(child.ChildValueId, values, mapValues, listValues)));
                break;
            case Value.KindOneofCase.None:
                break;
            default:
                throw new InvalidOperationException($"Unknown dashboard value kind '{record.ValueKind}'.");
        }

        return value;
    }

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

    private sealed record StoredResource(Resource Resource, int ReplicaIndex);

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
        public required long ValueId { get; init; }
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
        public long? ParameterValueId { get; init; }
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

    private sealed class ValueRecord
    {
        public required long ValueId { get; init; }
        public required int ValueKind { get; init; }
        public string? StringValue { get; init; }
        public double? NumberValue { get; init; }
        public bool? BoolValue { get; init; }
    }

    private sealed class MapValueRecord
    {
        public required long ParentValueId { get; init; }
        public required string MapKey { get; init; }
        public required long ChildValueId { get; init; }
    }

    private sealed class ListValueRecord
    {
        public required long ParentValueId { get; init; }
        public required long ChildValueId { get; init; }
    }
}