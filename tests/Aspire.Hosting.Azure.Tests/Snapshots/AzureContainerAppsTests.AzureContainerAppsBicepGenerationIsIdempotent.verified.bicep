@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param api_identity_outputs_id string

@secure()
param secret_value string

param kv_outputs_name string

param api_identity_outputs_clientid string

resource kv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: kv_outputs_name
}

resource kv_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'secret'
  parent: kv
}

resource existingKv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: 'existingKvName'
  scope: resourceGroup('existingRgName')
}

resource existingKv_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'secret'
  parent: existingKv
}

resource api 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'top-secret'
          value: secret_value
        }
        {
          name: 'top-secret2'
          identity: api_identity_outputs_id
          keyVaultUrl: kv_secret.properties.secretUri
        }
        {
          name: 'existing-top-secret'
          identity: api_identity_outputs_id
          keyVaultUrl: existingKv_secret.properties.secretUri
        }
      ]
      activeRevisionsMode: 'Single'
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'api'
          env: [
            {
              name: 'TOP_SECRET'
              secretRef: 'top-secret'
            }
            {
              name: 'TOP_SECRET2'
              secretRef: 'top-secret2'
            }
            {
              name: 'EXISTING_TOP_SECRET'
              secretRef: 'existing-top-secret'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: api_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${api_identity_outputs_id}': { }
    }
  }
}