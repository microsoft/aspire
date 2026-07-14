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
        var resourceRecords = connection.Query<ResourceRecord>("""
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
            """);

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
            LoadEnvironment(connection, resource);
            LoadUrls(connection, resource);
            LoadVolumes(connection, resource);
            LoadHealthReports(connection, resource);
            LoadRelationships(connection, resource);
            LoadProperties(connection, resource);
            LoadCommands(connection, resource);

            yield return new StoredResource(resource, record.ReplicaIndex);
        }
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

    private static void LoadEnvironment(SqliteConnection connection, Resource resource)
    {
        foreach (var record in connection.Query<EnvironmentRecord>("""
            SELECT name AS Name, value AS Value, is_from_spec AS IsFromSpec
            FROM dashboard_resource_environment
            WHERE resource_name = @ResourceName
            ORDER BY ordinal;
            """, new { ResourceName = resource.Name }))
        {
            var item = new EnvironmentVariable { Name = record.Name, IsFromSpec = record.IsFromSpec };
            if (record.Value is not null)
            {
                item.Value = record.Value;
            }
            resource.Environment.Add(item);
        }
    }

    private static void LoadUrls(SqliteConnection connection, Resource resource)
    {
        foreach (var record in connection.Query<UrlRecord>("""
            SELECT
                endpoint_name AS EndpointName,
                full_url AS FullUrl,
                is_internal AS IsInternal,
                is_inactive AS IsInactive,
                display_sort_order AS DisplaySortOrder,
                display_name AS DisplayName
            FROM dashboard_resource_urls
            WHERE resource_name = @ResourceName
            ORDER BY ordinal;
            """, new { ResourceName = resource.Name }))
        {
            var item = new Url
            {
                FullUrl = record.FullUrl,
                IsInternal = record.IsInternal,
                IsInactive = record.IsInactive,
                DisplayProperties = new UrlDisplayProperties { SortOrder = record.DisplaySortOrder, DisplayName = record.DisplayName }
            };
            if (record.EndpointName is not null)
            {
                item.EndpointName = record.EndpointName;
            }
            resource.Urls.Add(item);
        }
    }

    private static void LoadVolumes(SqliteConnection connection, Resource resource)
    {
        resource.Volumes.Add(connection.Query<Volume>("""
            SELECT source AS Source, target AS Target, mount_type AS MountType, is_read_only AS IsReadOnly
            FROM dashboard_resource_volumes
            WHERE resource_name = @ResourceName
            ORDER BY ordinal;
            """, new { ResourceName = resource.Name }));
    }

    private static void LoadHealthReports(SqliteConnection connection, Resource resource)
    {
        foreach (var record in connection.Query<HealthReportRecord>("""
            SELECT
                status AS Status,
                key AS Key,
                description AS Description,
                exception AS Exception,
                last_run_at_seconds AS LastRunAtSeconds,
                last_run_at_nanos AS LastRunAtNanos
            FROM dashboard_resource_health_reports
            WHERE resource_name = @ResourceName
            ORDER BY ordinal;
            """, new { ResourceName = resource.Name }))
        {
            var item = new HealthReport { Key = record.Key, Description = record.Description, Exception = record.Exception };
            if (record.Status is not null)
            {
                item.Status = (HealthStatus)record.Status.Value;
            }
            if (record.LastRunAtSeconds is not null)
            {
                item.LastRunAt = CreateTimestamp(record.LastRunAtSeconds.Value, record.LastRunAtNanos);
            }
            resource.HealthReports.Add(item);
        }
    }

    private static void LoadRelationships(SqliteConnection connection, Resource resource)
    {
        resource.Relationships.Add(connection.Query<ResourceRelationship>("""
            SELECT related_resource_name AS ResourceName, relationship_type AS Type
            FROM dashboard_resource_relationships
            WHERE resource_name = @ResourceName
            ORDER BY ordinal;
            """, new { ResourceName = resource.Name }));
    }

    private static void LoadProperties(SqliteConnection connection, Resource resource)
    {
        foreach (var record in connection.Query<PropertyRecord>("""
            SELECT
                name AS Name,
                display_name AS DisplayName,
                value_id AS ValueId,
                is_sensitive AS IsSensitive,
                is_highlighted AS IsHighlighted,
                sort_order AS SortOrder
            FROM dashboard_resource_properties
            WHERE resource_name = @ResourceName
            ORDER BY ordinal;
            """, new { ResourceName = resource.Name }))
        {
            var item = new ResourceProperty
            {
                Name = record.Name,
                Value = LoadValue(connection, record.ValueId),
                IsHighlighted = record.IsHighlighted
            };
            if (record.DisplayName is not null)
            {
                item.DisplayName = record.DisplayName;
            }
            if (record.IsSensitive is not null)
            {
                item.IsSensitive = record.IsSensitive.Value;
            }
            if (record.SortOrder is not null)
            {
                item.SortOrder = record.SortOrder.Value;
            }
            resource.Properties.Add(item);
        }
    }

    private static void LoadCommands(SqliteConnection connection, Resource resource)
    {
#pragma warning disable CS0612 // ResourceCommand.Parameter must be restored for compatibility with older AppHosts.
        foreach (var record in connection.Query<CommandRecord>("""
            SELECT
                ordinal AS Ordinal,
                name AS Name,
                display_name AS DisplayName,
                confirmation_message AS ConfirmationMessage,
                parameter_value_id AS ParameterValueId,
                is_highlighted AS IsHighlighted,
                icon_name AS IconName,
                icon_variant AS IconVariant,
                display_description AS DisplayDescription,
                state AS State
            FROM dashboard_resource_commands
            WHERE resource_name = @ResourceName
            ORDER BY ordinal;
            """, new { ResourceName = resource.Name }))
        {
            var command = new ResourceCommand
            {
                Name = record.Name,
                DisplayName = record.DisplayName,
                IsHighlighted = record.IsHighlighted,
                State = (ResourceCommandState)record.State
            };
            if (record.ConfirmationMessage is not null)
            {
                command.ConfirmationMessage = record.ConfirmationMessage;
            }
            if (record.ParameterValueId is not null)
            {
                command.Parameter = LoadValue(connection, record.ParameterValueId.Value);
            }
            if (record.IconName is not null)
            {
                command.IconName = record.IconName;
            }
            if (record.IconVariant is not null)
            {
                command.IconVariant = (IconVariant)record.IconVariant.Value;
            }
            if (record.DisplayDescription is not null)
            {
                command.DisplayDescription = record.DisplayDescription;
            }

            LoadCommandInputs(connection, resource.Name, record.Ordinal, command);
            resource.Commands.Add(command);
        }
#pragma warning restore CS0612
    }

    private static void LoadCommandInputs(SqliteConnection connection, string resourceName, int commandOrdinal, ResourceCommand command)
    {
        foreach (var record in connection.Query<InputRecord>("""
            SELECT
                ordinal AS Ordinal,
                label AS Label,
                placeholder AS Placeholder,
                input_type AS InputType,
                required AS Required,
                value AS Value,
                description AS Description,
                enable_description_markdown AS EnableDescriptionMarkdown,
                max_length AS MaxLength,
                allow_custom_choice AS AllowCustomChoice,
                loading AS Loading,
                update_state_on_change AS UpdateStateOnChange,
                name AS Name,
                disabled AS Disabled,
                max_file_size AS MaxFileSize,
                allow_multiple_files AS AllowMultipleFiles,
                file_filter AS FileFilter
            FROM dashboard_resource_command_inputs
            WHERE resource_name = @ResourceName AND command_ordinal = @CommandOrdinal
            ORDER BY ordinal;
            """, new { ResourceName = resourceName, CommandOrdinal = commandOrdinal }))
        {
            var input = new InteractionInput
            {
                Label = record.Label,
                Placeholder = record.Placeholder,
                InputType = (InputType)record.InputType,
                Required = record.Required,
                Value = record.Value,
                Description = record.Description,
                EnableDescriptionMarkdown = record.EnableDescriptionMarkdown,
                MaxLength = record.MaxLength,
                AllowCustomChoice = record.AllowCustomChoice,
                Loading = record.Loading,
                UpdateStateOnChange = record.UpdateStateOnChange,
                Name = record.Name,
                Disabled = record.Disabled,
                MaxFileSize = record.MaxFileSize,
                AllowMultipleFiles = record.AllowMultipleFiles,
                FileFilter = record.FileFilter
            };

            foreach (var option in connection.Query<OptionRecord>("""
                SELECT option_key AS OptionKey, option_value AS OptionValue
                FROM dashboard_resource_command_input_options
                WHERE resource_name = @ResourceName AND command_ordinal = @CommandOrdinal AND input_ordinal = @InputOrdinal;
                """, new { ResourceName = resourceName, CommandOrdinal = commandOrdinal, InputOrdinal = record.Ordinal }))
            {
                input.Options.Add(option.OptionKey, option.OptionValue);
            }
            input.ValidationErrors.Add(connection.Query<string>("""
                SELECT validation_error
                FROM dashboard_resource_command_input_validation_errors
                WHERE resource_name = @ResourceName AND command_ordinal = @CommandOrdinal AND input_ordinal = @InputOrdinal
                ORDER BY ordinal;
                """, new { ResourceName = resourceName, CommandOrdinal = commandOrdinal, InputOrdinal = record.Ordinal }));
            command.ArgumentInputs.Add(input);
        }
    }

    private static Value LoadValue(SqliteConnection connection, long valueId)
    {
        var record = connection.QuerySingle<ValueRecord>("""
            SELECT
                value_id AS ValueId,
                value_kind AS ValueKind,
                string_value AS StringValue,
                number_value AS NumberValue,
                bool_value AS BoolValue
            FROM dashboard_values
            WHERE value_id = @ValueId;
            """, new { ValueId = valueId });

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
                foreach (var child in connection.Query<MapValueRecord>("""
                    SELECT map_key AS MapKey, child_value_id AS ChildValueId
                    FROM dashboard_value_map_entries
                    WHERE parent_value_id = @ValueId
                    ORDER BY ordinal;
                    """, new { ValueId = valueId }))
                {
                    value.StructValue.Fields.Add(child.MapKey, LoadValue(connection, child.ChildValueId));
                }
                break;
            case Value.KindOneofCase.ListValue:
                value.ListValue = new ListValue();
                foreach (var childValueId in connection.Query<long>("""
                    SELECT child_value_id
                    FROM dashboard_value_list_items
                    WHERE parent_value_id = @ValueId
                    ORDER BY ordinal;
                    """, new { ValueId = valueId }))
                {
                    value.ListValue.Values.Add(LoadValue(connection, childValueId));
                }
                break;
            case Value.KindOneofCase.None:
                break;
            default:
                throw new InvalidOperationException($"Unknown dashboard value kind '{record.ValueKind}'.");
        }

        return value;
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
        public required string Name { get; init; }
        public string? Value { get; init; }
        public required bool IsFromSpec { get; init; }
    }

    private sealed class UrlRecord
    {
        public string? EndpointName { get; init; }
        public required string FullUrl { get; init; }
        public required bool IsInternal { get; init; }
        public required bool IsInactive { get; init; }
        public required int DisplaySortOrder { get; init; }
        public required string DisplayName { get; init; }
    }

    private sealed class HealthReportRecord
    {
        public int? Status { get; init; }
        public required string Key { get; init; }
        public required string Description { get; init; }
        public required string Exception { get; init; }
        public long? LastRunAtSeconds { get; init; }
        public int? LastRunAtNanos { get; init; }
    }

    private sealed class PropertyRecord
    {
        public required string Name { get; init; }
        public string? DisplayName { get; init; }
        public required long ValueId { get; init; }
        public bool? IsSensitive { get; init; }
        public required bool IsHighlighted { get; init; }
        public int? SortOrder { get; init; }
    }

    private sealed class CommandRecord
    {
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
        public required string OptionKey { get; init; }
        public required string OptionValue { get; init; }
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
        public required string MapKey { get; init; }
        public required long ChildValueId { get; init; }
    }
}