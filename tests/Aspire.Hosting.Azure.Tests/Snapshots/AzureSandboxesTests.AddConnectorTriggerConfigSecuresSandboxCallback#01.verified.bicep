@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param gateway_outputs_name string

param new_file_callbackUrl string

resource gateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' existing = {
  name: gateway_outputs_name
}

resource new_file 'Microsoft.Web/connectorGateways/triggerConfigs@2026-05-01-preview' = {
  name: 'sharepoint-new-file'
  properties: {
    description: 'Posts new SharePoint files to the sandbox.'
    connectionDetails: {
      connectorName: 'sharepointonline'
      connectionName: 'sharepoint'
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
      callbackUrl: new_file_callbackUrl
      httpMethod: 'POST'
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