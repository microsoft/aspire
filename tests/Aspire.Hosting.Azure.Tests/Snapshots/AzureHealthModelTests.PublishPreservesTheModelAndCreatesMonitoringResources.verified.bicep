targetScope = 'resourceGroup'

@description('Azure region supporting Azure Monitor health models and managed Prometheus.')
param location string = resourceGroup().location

@description('Health model name; the root entity is created with this name.')
param healthModelName string = 'sample-health'

var definitionRootName = 'sample-health'
var prefix = '${take(healthModelName, 40)}-${uniqueString(resourceGroup().id, healthModelName)}'
var entityDefinitions = json('[{"name":"resource-api","displayName":"API\\u0027s \${literal} label","canvasPosition":{"x":224.25,"y":-180.5},"impact":"Limited","healthObjective":99.95,"dependencies":{"aggregationType":"MaxNotHealthy","ignoreUnknown":true,"unit":"Percentage","unhealthyThreshold":75.5,"degradedThreshold":25},"signals":[{"name":"signal-a0465975c3cd65f2","displayName":"api_check","signalKind":"PrometheusMetricsQuery","signalDefinitionName":"signal-a0465975c3cd65f2"},{"name":"signal-334b02c455746707","displayName":"resource-state","signalKind":"PrometheusMetricsQuery","signalDefinitionName":"signal-334b02c455746707"}]},{"name":"sample-health","displayName":"AppHost","canvasPosition":{"x":0,"y":0},"impact":"Standard","healthObjective":null,"dependencies":{"aggregationType":"WorstOf","ignoreUnknown":true},"signals":[]}]')
var signalConfigurations = json('[{"name":"signal-a0465975c3cd65f2","displayName":"API\\u0027s \${literal} label: api_check","queryText":"min(((aspire_health_status{resource_name=\\u0022api\\u0022,health_check=\\u0022api_check\\u0022,replica_index=\\u00221\\u0022} \\u003E= 0) \\u003C= 2) and (time() - timestamp(aspire_health_status{resource_name=\\u0022api\\u0022,health_check=\\u0022api_check\\u0022,replica_index=\\u00221\\u0022}) \\u003C 180))"},{"name":"signal-334b02c455746707","displayName":"API\\u0027s \${literal} label: resource-state","queryText":"min(((aspire_health_status{resource_name=\\u0022api\\u0022,health_check=\\u0022resource-state\\u0022,replica_index=\\u00221\\u0022} \\u003E= 0) \\u003C= 2) and (time() - timestamp(aspire_health_status{resource_name=\\u0022api\\u0022,health_check=\\u0022resource-state\\u0022,replica_index=\\u00221\\u0022}) \\u003C 180))"}]')
var relationshipDefinitions = json('[{"name":"relationship-a209a7c32ab60a44","parentEntityName":"sample-health","childEntityName":"resource-api"}]')

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

resource healthModel 'Microsoft.CloudHealth/healthmodels@2026-09-01-preview' = {
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

resource authentication 'Microsoft.CloudHealth/healthmodels/authenticationsettings@2026-09-01-preview' = {
  parent: healthModel
  name: 'workspace-reader'
  properties: {
    authenticationKind: 'ManagedIdentity'
    managedIdentityName: 'SystemAssigned'
  }
}

resource signalDefinitions 'Microsoft.CloudHealth/healthmodels/signaldefinitions@2026-09-01-preview' = [for signal in signalConfigurations: {
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

resource modelEntities 'Microsoft.CloudHealth/healthmodels/entities@2026-09-01-preview' = [for entity in entityDefinitions: {
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

resource modelRelationships 'Microsoft.CloudHealth/healthmodels/relationships@2026-09-01-preview' = [for relationship in relationshipDefinitions: {
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