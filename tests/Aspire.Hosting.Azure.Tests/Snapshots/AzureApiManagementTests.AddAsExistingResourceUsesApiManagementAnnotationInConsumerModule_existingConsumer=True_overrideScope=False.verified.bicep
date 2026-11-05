@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_name string

param apim_subscription string

param apim_resource_group string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' existing = {
  name: apim_name
  scope: resourceGroup(apim_subscription, apim_resource_group)
}

output gatewayUrl string = apim.properties.gatewayUrl