@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_name string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' existing = {
  name: apim_name
  scope: resourceGroup('override-gateway-subscription', 'override-gateway-rg')
}

output gatewayUrl string = apim.properties.gatewayUrl