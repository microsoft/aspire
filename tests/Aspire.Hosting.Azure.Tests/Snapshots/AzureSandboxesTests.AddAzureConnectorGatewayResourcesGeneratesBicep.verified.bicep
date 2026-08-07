@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param worker_identity_outputs_principalid string

resource gateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' = {
  name: take('gateway${uniqueString(resourceGroup().id)}', 24)
  location: resourceGroup().location
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'aspire-resource-name': 'gateway'
  }
}

resource office365 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' = {
  name: 'office365-outlook'
  properties: {
    displayName: 'Office 365 Outlook'
    connectorName: 'office365'
  }
  parent: gateway
}

resource worker_access 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'worker-acl'
  location: resourceGroup().location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: '11111111-1111-1111-1111-111111111111'
        tenantId: '22222222-2222-2222-2222-222222222222'
      }
    }
  }
  parent: office365
}

resource worker_identity_access 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'worker-identity-acl'
  location: resourceGroup().location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: worker_identity_outputs_principalid
        tenantId: tenant().tenantId
      }
    }
  }
  parent: office365
}

resource outlook_mcp 'Microsoft.Web/connectorGateways/mcpserverConfigs@2026-05-01-preview' = {
  name: 'outlook-tools'
  kind: 'ManagedMcpServer'
  properties: {
    description: 'Allow-listed Outlook tools.'
    state: 'Enabled'
    connectors: [
      {
        name: 'office365'
        connectionName: 'office365-outlook'
        displayName: 'Office 365 Outlook'
        description: 'Read-only Outlook operations.'
        operations: [
          {
            name: 'GetEmailsV3'
            displayName: 'Get emails'
            description: 'Reads recent emails.'
          }
        ]
      }
    ]
  }
  parent: gateway
  dependsOn: [
    office365
  ]
}

output id string = gateway.id

output name string = gateway.name

output principalId string = gateway.identity.principalId

output tenantId string = gateway.identity.tenantId