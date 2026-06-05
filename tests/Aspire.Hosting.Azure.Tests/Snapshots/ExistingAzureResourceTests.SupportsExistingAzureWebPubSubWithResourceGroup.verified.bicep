@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource webPubSub 'Microsoft.SignalRService/webPubSub@2024-03-01' existing = {
  name: existingResourceName
}

output endpoint string = 'https://${webPubSub.properties.hostName}'

output name string = webPubSub.name

output id string = webPubSub.id