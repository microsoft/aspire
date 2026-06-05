@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource signalR 'Microsoft.SignalRService/signalR@2024-03-01' existing = {
  name: existingResourceName
}

output hostName string = signalR.properties.hostName

output name string = signalR.name

output id string = signalR.id