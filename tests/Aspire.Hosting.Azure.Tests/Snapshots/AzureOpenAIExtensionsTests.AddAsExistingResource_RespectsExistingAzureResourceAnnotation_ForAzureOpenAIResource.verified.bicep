@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existing_openai_name string

param existing_openai_rg string

resource test_openai 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: existing_openai_name
  scope: resourceGroup(existing_openai_rg)
}