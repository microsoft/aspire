@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingSchedulerName string

resource dts 'Microsoft.DurableTask/schedulers@2025-11-01' existing = {
  name: existingSchedulerName
}

resource myhub 'Microsoft.DurableTask/schedulers/taskhubs@2025-11-01' = {
  name: 'myhub'
  parent: dts
}

output schedulerEndpoint string = dts.properties.endpoint

output name string = existingSchedulerName