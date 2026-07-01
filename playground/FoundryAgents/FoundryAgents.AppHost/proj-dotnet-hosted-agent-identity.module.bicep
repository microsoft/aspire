@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource proj_dotnet_hosted_agent_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('proj_dotnet_hosted_agent_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = proj_dotnet_hosted_agent_identity.id

output clientId string = proj_dotnet_hosted_agent_identity.properties.clientId

output principalId string = proj_dotnet_hosted_agent_identity.properties.principalId

output principalName string = proj_dotnet_hosted_agent_identity.name

output name string = proj_dotnet_hosted_agent_identity.name