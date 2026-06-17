@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param gateway_outputs_name string

param newfile_callbackUrl string

resource gateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' existing = {
  name: gateway_outputs_name
}

resource newfile 'Microsoft.Web/connectorGateways/triggerConfigs@2026-05-01-preview' = {
  name: 'new-file'
  properties: {
    description: 'Post new SharePoint files to the sandbox listener.'
    connectionDetails: {
      connectorName: 'sharepointonline'
      connectionName: 'sharepoint-conn'
    }
    metadata: {
      sandboxResource: 'listener'
      sandboxEndpoint: 'http'
    }
    notificationDetails: {
      authentication: {
        type: 'ManagedServiceIdentity'
        audience: 'https://auth.adcproxy.io/'
      }
      callbackUrl: newfile_callbackUrl
      httpMethod: 'Post'
    }
    operationName: 'GetOnNewFileItems'
    parameters: [
      {
        name: 'dataset'
        value: 'https://contoso.sharepoint.com/sites/demo'
      }
      {
        name: 'table'
        value: 'Documents'
      }
    ]
    state: 'Enabled'
  }
  parent: gateway
}