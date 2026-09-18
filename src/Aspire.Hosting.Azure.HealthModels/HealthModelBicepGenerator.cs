// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.HealthModels;

namespace Aspire.Hosting.Azure;

internal static class HealthModelBicepGenerator
{
    internal const string HealthApiVersion = "2026-09-01-preview";
    internal const string MetricName = "aspire_health_status";
    internal const int MaximumSampleAgeSeconds = 180;

    internal static string Generate(HealthModelDocument document)
    {
        HealthModelContract.Validate(document);
        var entities = new JsonArray();
        var signals = new JsonArray();
        foreach (var entity in document.Entities.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var assignments = new JsonArray();
            foreach (var signal in entity.LocalSignals.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (signal.Kind != SignalKind.External || entity.AspireResourceName is null || entity.ReplicaIndex is null)
                {
                    throw new InvalidDataException($"Entity '{entity.Name}' contains a signal without a supported local health metric binding.");
                }

                var definitionName = StableName("signal", entity.Name, signal.Name);
                signals.Add(new JsonObject
                {
                    ["name"] = definitionName,
                    ["displayName"] = Truncate($"{entity.DisplayName}: {signal.Name}", 260),
                    ["queryText"] = CreateQuery(entity.AspireResourceName, entity.ReplicaIndex.Value, signal.Name)
                });
                assignments.Add(new JsonObject
                {
                    ["name"] = definitionName,
                    ["displayName"] = Truncate(signal.Name, 260),
                    ["signalKind"] = "PrometheusMetricsQuery",
                    ["signalDefinitionName"] = definitionName
                });
            }

            var dependencies = new JsonObject
            {
                ["aggregationType"] = entity.Dependencies.AggregationType.ToString(),
                ["ignoreUnknown"] = entity.Dependencies.IgnoreUnknown
            };
            if (entity.Dependencies.AggregationType != DependenciesAggregationType.WorstOf)
            {
                dependencies["unit"] = entity.Dependencies.Unit.ToString();
                dependencies["unhealthyThreshold"] = entity.Dependencies.UnhealthyThreshold;
                if (entity.Dependencies.DegradedThreshold is { } degraded)
                {
                    dependencies["degradedThreshold"] = degraded;
                }
            }
            entities.Add(new JsonObject
            {
                ["name"] = entity.Name,
                ["displayName"] = entity.DisplayName,
                ["canvasPosition"] = new JsonObject { ["x"] = entity.CanvasPosition.X, ["y"] = entity.CanvasPosition.Y },
                ["impact"] = entity.Impact.ToString(),
                ["healthObjective"] = entity.HealthObjective,
                ["dependencies"] = dependencies,
                ["signals"] = assignments
            });
        }

        var relationships = new JsonArray(document.Relationships
            .OrderBy(r => r.ParentEntityName, StringComparer.Ordinal).ThenBy(r => r.ChildEntityName, StringComparer.Ordinal)
            .Select(r => (JsonNode)new JsonObject
            {
                ["name"] = StableName("relationship", r.ParentEntityName, r.ChildEntityName),
                ["parentEntityName"] = r.ParentEntityName,
                ["childEntityName"] = r.ChildEntityName
            }).ToArray());

        // json() preserves fractional coordinates and thresholds; Bicep has no floating-point literal
        // syntax. Do not round the browser's saved geometry just to fit Bicep's native int type.
        return $$"""
            targetScope = 'resourceGroup'

            @description('Azure region supporting Azure Monitor health models and managed Prometheus.')
            param location string = resourceGroup().location

            @description('Health model name; the root entity is created with this name.')
            param healthModelName string = {{Literal(document.Name)}}

            var definitionRootName = {{Literal(document.Name)}}
            var prefix = '${take(healthModelName, 40)}-${uniqueString(resourceGroup().id, healthModelName)}'
            var entityDefinitions = json({{Literal(entities.ToJsonString())}})
            var signalConfigurations = json({{Literal(signals.ToJsonString())}})
            var relationshipDefinitions = json({{Literal(relationships.ToJsonString())}})

            resource workspace 'Microsoft.Monitor/accounts@2023-04-03' = {
              name: '${prefix}-amw'
              location: location
              properties: {
                publicNetworkAccess: 'Enabled'
              }
            }

            resource ingestionEndpoint 'Microsoft.Insights/dataCollectionEndpoints@2023-03-11' = {
              name: '${prefix}-dce'
              location: location
              kind: 'Linux'
              properties: {
                networkAcls: {
                  publicNetworkAccess: 'Enabled'
                }
              }
            }

            resource ingestionRule 'Microsoft.Insights/dataCollectionRules@2023-03-11' = {
              name: '${prefix}-dcr'
              location: location
              kind: 'Linux'
              properties: {
                dataCollectionEndpointId: ingestionEndpoint.id
                dataSources: {
                  prometheusForwarder: [
                    {
                      name: 'health-metrics'
                      streams: [
                        'Microsoft-PrometheusMetrics'
                      ]
                    }
                  ]
                }
                destinations: {
                  monitoringAccounts: [
                    {
                      name: 'health-workspace'
                      accountResourceId: workspace.id
                    }
                  ]
                }
                dataFlows: [
                  {
                    streams: [
                      'Microsoft-PrometheusMetrics'
                    ]
                    destinations: [
                      'health-workspace'
                    ]
                  }
                ]
              }
            }

            resource collectorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
              name: '${prefix}-collector'
              location: location
            }

            resource metricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
              name: guid(ingestionRule.id, collectorIdentity.id, '3913510d-42f4-4e42-8a64-420c390055eb')
              scope: ingestionRule
              properties: {
                principalId: collectorIdentity.properties.principalId
                principalType: 'ServicePrincipal'
                roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
              }
            }

            resource healthModel 'Microsoft.CloudHealth/healthmodels@{{HealthApiVersion}}' = {
              name: healthModelName
              location: location
              identity: {
                type: 'SystemAssigned'
              }
              properties: {}
            }

            resource modelReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
              name: guid(workspace.id, healthModel.id, '43d0d8ad-25c7-4714-9337-8ba259a9fe05')
              scope: workspace
              properties: {
                principalId: healthModel.identity.principalId
                principalType: 'ServicePrincipal'
                roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '43d0d8ad-25c7-4714-9337-8ba259a9fe05')
              }
            }

            resource authentication 'Microsoft.CloudHealth/healthmodels/authenticationsettings@{{HealthApiVersion}}' = {
              parent: healthModel
              name: 'workspace-reader'
              properties: {
                authenticationKind: 'ManagedIdentity'
                managedIdentityName: 'SystemAssigned'
              }
            }

            resource signalDefinitions 'Microsoft.CloudHealth/healthmodels/signaldefinitions@{{HealthApiVersion}}' = [for signal in signalConfigurations: {
              parent: healthModel
              name: signal.name
              properties: {
                displayName: signal.displayName
                signalKind: 'PrometheusMetricsQuery'
                queryText: signal.queryText
                refreshInterval: 'PT1M'
                timeGrain: 'PT1M'
                evaluationRules: {
                  degradedRule: {
                    operator: 'LessThan'
                    threshold: 2
                  }
                  unhealthyRule: {
                    operator: 'LessThan'
                    threshold: 1
                  }
                }
              }
            }]

            resource modelEntities 'Microsoft.CloudHealth/healthmodels/entities@{{HealthApiVersion}}' = [for entity in entityDefinitions: {
              parent: healthModel
              name: entity.name == definitionRootName ? healthModel.name : entity.name
              properties: union({
                displayName: entity.displayName
                canvasPosition: entity.canvasPosition
                impact: entity.impact
                signalGroups: union({
                  dependencies: entity.dependencies
                }, empty(entity.signals) ? {} : {
                  azureMonitorWorkspace: {
                    authenticationSetting: authentication.name
                    azureMonitorWorkspaceResourceId: workspace.id
                    signals: entity.signals
                  }
                })
              }, entity.healthObjective == null ? {} : {
                healthObjective: entity.healthObjective
              })
              dependsOn: [
                modelReader
                signalDefinitions
              ]
            }]

            resource modelRelationships 'Microsoft.CloudHealth/healthmodels/relationships@{{HealthApiVersion}}' = [for relationship in relationshipDefinitions: {
              parent: healthModel
              name: relationship.name
              properties: {
                parentEntityName: relationship.parentEntityName == definitionRootName ? healthModel.name : relationship.parentEntityName
                childEntityName: relationship.childEntityName
              }
              dependsOn: [
                modelEntities
              ]
            }]

            output healthModelId string = healthModel.id
            output workspaceId string = workspace.id
            output dataCollectionEndpointId string = ingestionEndpoint.id
            output dataCollectionRuleId string = ingestionRule.id
            output remoteWriteEndpoint string = '${ingestionEndpoint.properties.metricsIngestion.endpoint}/dataCollectionRules/${ingestionRule.properties.immutableId}/streams/Microsoft-PrometheusMetrics/api/v1/write?api-version=2023-04-24'
            output collectorIdentityId string = collectorIdentity.id
            output collectorClientId string = collectorIdentity.properties.clientId
            """;
    }

    internal static string CreateQuery(string resourceName, int replicaIndex, string healthCheck)
    {
        var selector = $"{MetricName}{{resource_name=\"{PrometheusLabel(resourceName)}\",health_check=\"{PrometheusLabel(healthCheck)}\",replica_index=\"{replicaIndex.ToString(CultureInfo.InvariantCulture)}\"}}";
        // Preserve .NET HealthStatus (0 unhealthy, 1 degraded, 2 healthy). Negative/invalid samples and
        // stale series produce no result, not a manufactured healthy value. PromQL comparisons without
        // 'bool' retain the observed value so the reusable definition can apply both thresholds.
        return $"min((({selector} >= 0) <= 2) and (time() - timestamp({selector}) < {MaximumSampleAgeSeconds}))";
    }

    private static string PrometheusLabel(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string StableName(string prefix, string first, string second)
    {
        var value = JsonSerializer.Serialize(new[] { first, second });
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(value));
        return $"{prefix}-{hash.ToString("x16", CultureInfo.InvariantCulture)}";
    }

    private static string Literal(string value) => "'" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal)
        .Replace("${", "\\${", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "'";

    private static string Truncate(string value, int maximumLength) => value.Length <= maximumLength ? value : value[..maximumLength];
}
