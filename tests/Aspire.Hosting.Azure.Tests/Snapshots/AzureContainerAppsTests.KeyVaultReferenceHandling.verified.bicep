@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param api_identity_outputs_id string

param mydb_kv_outputs_name string

param mydb_outputs_connectionstring string

param kvName string

param sharedRg string

param api_identity_outputs_clientid string

resource mydb_kv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: mydb_kv_outputs_name
}

resource mydb_kv_connectionstrings__mydb 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'connectionstrings--mydb'
  parent: mydb_kv
}

resource mydb_kv_primaryaccesskey__mydb 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'primaryaccesskey--mydb'
  parent: mydb_kv
}

resource existingKv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: kvName
  scope: resourceGroup(sharedRg)
}

resource existingKv_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'secret'
  parent: existingKv
}

resource api 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--mydb'
          identity: api_identity_outputs_id
          keyVaultUrl: mydb_kv_connectionstrings__mydb.properties.secretUri
        }
        {
          name: 'mydb-accountkey'
          identity: api_identity_outputs_id
          keyVaultUrl: mydb_kv_primaryaccesskey__mydb.properties.secretUri
        }
        {
          name: 'mydb-connectionstring'
          identity: api_identity_outputs_id
          keyVaultUrl: mydb_kv_connectionstrings__mydb.properties.secretUri
        }
        {
          name: 'secret-value'
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
          image: 'image:latest'
          name: 'api'
          env: [
            {
              name: 'ConnectionStrings__mydb'
              secretRef: 'connectionstrings--mydb'
            }
            {
              name: 'MYDB_URI'
              value: mydb_outputs_connectionstring
            }
            {
              name: 'MYDB_ACCOUNTKEY'
              secretRef: 'mydb-accountkey'
            }
            {
              name: 'MYDB_CONNECTIONSTRING'
              secretRef: 'mydb-connectionstring'
            }
            {
              name: 'SECRET_VALUE'
              secretRef: 'secret-value'
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
