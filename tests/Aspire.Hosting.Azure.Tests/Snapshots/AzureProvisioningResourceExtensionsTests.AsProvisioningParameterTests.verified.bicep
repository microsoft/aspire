@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param endpointAddressParam string

param someExpressionParam string

resource app 'Microsoft.App/containerApps@2025-07-01' = {
  name: take('app-${uniqueString(resourceGroup().id)}', 32)
  location: location
  properties: {
    template: {
      scale: {
        rules: [
          {
            name: 'temp'
            custom: {
              type: 'external'
              metadata: {
                address: endpointAddressParam
                someExpression: someExpressionParam
              }
            }
          }
        ]
      }
    }
  }
}