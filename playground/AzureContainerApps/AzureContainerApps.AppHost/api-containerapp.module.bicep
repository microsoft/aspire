@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param infra_outputs_azure_container_apps_environment_default_domain string

param infra_outputs_azure_container_apps_environment_id string

param api_containerimage string

param api_identity_outputs_id string

param api_containerport string

param storage_outputs_blobendpoint string

@secure()
param cache_password_value string

param account_kv_outputs_name string

param account_outputs_connectionstring string

@secure()
param secretparam_value string

param api_identity_outputs_principalname string

param api_identity_outputs_clientid string

param certificateName string

param customDomain string

param infra_outputs_azure_container_registry_endpoint string

param infra_outputs_azure_container_registry_managed_identity_id string

resource account_kv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: account_kv_outputs_name
}

resource account_kv_connectionstrings__account 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'connectionstrings--account'
  parent: account_kv
}

resource account_kv_primaryaccesskey__account 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'primaryaccesskey--account'
  parent: account_kv
}

resource api 'Microsoft.App/containerApps@2025-02-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--cache'
          value: 'cache:6379,password=${cache_password_value}'
        }
        {
          name: 'cache-password'
          value: cache_password_value
        }
        {
          name: 'cache-uri'
          value: 'redis://:${uriComponent(cache_password_value)}@cache:6379'
        }
        {
          name: 'connectionstrings--account'
          identity: api_identity_outputs_id
          keyVaultUrl: account_kv_connectionstrings__account.properties.secretUri
        }
        {
          name: 'account-accountkey'
          identity: api_identity_outputs_id
          keyVaultUrl: account_kv_primaryaccesskey__account.properties.secretUri
        }
        {
          name: 'account-connectionstring'
          identity: api_identity_outputs_id
          keyVaultUrl: account_kv_connectionstrings__account.properties.secretUri
        }
        {
          name: 'value'
          value: secretparam_value
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(api_containerport)
        transport: 'http'
        customDomains: [
          {
            name: customDomain
            bindingType: (certificateName != '') ? 'SniEnabled' : 'Disabled'
            certificateId: (certificateName != '') ? '${infra_outputs_azure_container_apps_environment_id}/managedCertificates/${certificateName}' : null
          }
        ]
      }
      registries: [
        {
          server: infra_outputs_azure_container_registry_endpoint
          identity: infra_outputs_azure_container_registry_managed_identity_id
        }
      ]
      runtime: {
        dotnet: {
          autoConfigureDataProtection: true
        }
      }
    }
    environmentId: infra_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: api_containerimage
          name: 'api'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'HTTP_PORTS'
              value: api_containerport
            }
            {
              name: 'ConnectionStrings__blobs'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'BLOBS_URI'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'ConnectionStrings__cache'
              secretRef: 'connectionstrings--cache'
            }
            {
              name: 'CACHE_HOST'
              value: 'cache'
            }
            {
              name: 'CACHE_PORT'
              value: '6379'
            }
            {
              name: 'CACHE_PASSWORD'
              secretRef: 'cache-password'
            }
            {
              name: 'CACHE_URI'
              secretRef: 'cache-uri'
            }
            {
              name: 'ConnectionStrings__account'
              secretRef: 'connectionstrings--account'
            }
            {
              name: 'ACCOUNT_URI'
              value: account_outputs_connectionstring
            }
            {
              name: 'ACCOUNT_ACCOUNTKEY'
              secretRef: 'account-accountkey'
            }
            {
              name: 'ACCOUNT_CONNECTIONSTRING'
              secretRef: 'account-connectionstring'
            }
            {
              name: 'VALUE'
              secretRef: 'value'
            }
            {
              name: 'AZURE_PRINCIPAL_NAME'
              value: api_identity_outputs_principalname
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
        minReplicas: 0
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${api_identity_outputs_id}': { }
      '${infra_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}