@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param worker_identity_outputs_principalid string

resource connectorGateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' = {
  name: 'location${uniqueString(resourceGroup().id, 'location')}'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: { }
  tags: {
    'aspire-resource-name': 'location'
  }
}

resource connectorConnection_location_office365_aeeb69f5db2f2579 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' = {
  name: 'office365-outlook'
  properties: {
    displayName: 'Office 365 Outlook'
    connectorName: 'office365'
  }
  parent: connectorGateway
}

resource connectorAccessPolicy_location_office365_worker_access_219548f1dffff16f 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'worker-acl'
  location: location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: '11111111-1111-1111-1111-111111111111'
        tenantId: '22222222-2222-2222-2222-222222222222'
      }
    }
  }
  parent: connectorConnection_location_office365_aeeb69f5db2f2579
}

resource connectorAccessPolicy_location_office365_worker_identity__b35655bad0e10beb 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'worker-identity-acl'
  location: location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: worker_identity_outputs_principalid
        tenantId: tenant().tenantId
      }
    }
  }
  parent: connectorConnection_location_office365_aeeb69f5db2f2579
}

resource connectorMcpServer_location_outlook_mcp_9fda0444956c6cd1 'Microsoft.Web/connectorGateways/mcpserverConfigs@2026-05-01-preview' = {
  name: 'outlook-tools'
  kind: 'ManagedMcpServer'
  properties: {
    description: 'Allow-listed Outlook tools.'
    state: 'Enabled'
    connectors: [
      {
        name: 'office365'
        connectionName: 'office365-outlook'
        displayName: 'office365'
        description: 'Read-only Outlook operations.'
        operations: [
          {
            name: 'GetEmailsV3'
            displayName: 'GetEmailsV3'
            description: 'Reads recent emails.'
          }
        ]
      }
    ]
  }
  parent: connectorGateway
  dependsOn: [
    connectorConnection_location_office365_aeeb69f5db2f2579
  ]
}

resource connectorMcpAccessPolicy_location_outlook_mcp_developer_access_146ddccf26dca673 'Microsoft.Web/connectorGateways/mcpserverConfigs/accessPolicies@2026-05-01-preview' = {
  name: '33333333-3333-3333-3333-333333333333'
  location: location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: '33333333-3333-3333-3333-333333333333'
        tenantId: '22222222-2222-2222-2222-222222222222'
      }
    }
    principalType: 'User'
  }
  parent: connectorMcpServer_location_outlook_mcp_9fda0444956c6cd1
}

output id string = connectorGateway.id

output name string = connectorGateway.name

output principalId string = connectorGateway.identity.principalId

output tenantId string = connectorGateway.identity.tenantId