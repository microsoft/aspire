@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

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

resource sharepoint 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' = {
  name: 'sharepoint-conn'
  properties: {
    displayName: 'sharepoint-conn'
    connectorName: 'sharepointonline'
  }
  parent: gateway
}

resource sharepoint_gateway_acl 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'gateway-acl'
  location: resourceGroup().location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: gateway.identity.principalId
        tenantId: gateway.identity.tenantId
      }
    }
  }
  parent: sharepoint
}

output id string = gateway.id

output name string = gateway.name

output principalId string = gateway.identity.principalId

output tenantId string = gateway.identity.tenantId