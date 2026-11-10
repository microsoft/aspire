@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_outputs_name string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' existing = {
  name: apim_outputs_name
}

output gatewayUrl string = apim.properties.gatewayUrl