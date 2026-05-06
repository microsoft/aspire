@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param proj_myproject_outputs_name string

param search_outputs_name string

resource proj_myproject 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' existing = {
  name: proj_myproject_outputs_name
}

resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: search_outputs_name
}

resource connection_dc5878fa0d0b490886a83d97407d0b9b 'Microsoft.CognitiveServices/accounts/projects/connections@2026-03-01' = {
  name: 'connection-dc5878fa0d0b490886a83d97407d0b9b'
  properties: {
    category: 'CognitiveSearch'
    metadata: {
      ApiType: 'Azure'
      ResourceId: search.id
      location: search.location
    }
    target: 'https://${search_outputs_name}.search.windows.net'
    authType: 'AAD'
  }
  parent: proj_myproject
}

output name string = 'connection-dc5878fa0d0b490886a83d97407d0b9b'

output id string = connection_dc5878fa0d0b490886a83d97407d0b9b.id