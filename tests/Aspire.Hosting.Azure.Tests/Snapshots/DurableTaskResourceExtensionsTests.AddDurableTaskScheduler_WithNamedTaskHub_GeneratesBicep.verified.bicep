@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Consumption'

resource dts 'Microsoft.DurableTask/schedulers@2025-11-01' = {
  name: 'dts'
  location: location
  properties: {
    sku: {
      name: sku
    }
    ipAllowlist: [
      '0.0.0.0/0'
    ]
  }
}

resource myhub 'Microsoft.DurableTask/schedulers/taskhubs@2025-11-01' = {
  name: 'MyCustomTaskHub'
  parent: dts
}

output schedulerEndpoint string = dts.properties.endpoint

output name string = 'dts'