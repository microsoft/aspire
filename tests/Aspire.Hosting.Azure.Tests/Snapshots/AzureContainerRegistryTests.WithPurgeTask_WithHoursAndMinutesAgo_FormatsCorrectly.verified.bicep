@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('acr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'acr'
  }
}

var purgeTaskCmd_0 = 'version: v1.1.0\nsteps:\n- cmd: acr purge --filter \'.*:.*\' --ago 2d3h6m --keep 3'

resource purgeOldImages_0 'Microsoft.ContainerRegistry/registries/tasks@2019-04-01' = {
  name: 'purgeOldImages'
  location: location
  properties: {
    platform: {
      os: 'Linux'
    }
    step: {
      type: 'EncodedTask'
      encodedTaskContent: base64(purgeTaskCmd_0)
    }
    trigger: {
      timerTriggers: [
        {
          schedule: '0 0 * * *'
          name: 'purgeOldImages_trigger'
        }
      ]
    }
  }
  parent: acr
}

output name string = acr.name

output loginServer string = acr.properties.loginServer