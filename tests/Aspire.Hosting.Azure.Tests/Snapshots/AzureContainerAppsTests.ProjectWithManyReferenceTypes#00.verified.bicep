@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

param api_containerimage string

param api_identity_outputs_id string

param api_containerport string

param mydb_outputs_connectionstring string

param storage_outputs_blobendpoint string

param pg_kv_outputs_name string

param pg_outputs_hostname string

param pg_username_value string

@secure()
param pg_password_value string

@secure()
param value0_value string

param value1_value string

@secure()
param cs_connectionstring string

@secure()
param pgc_password_value string

param api_identity_outputs_clientid string

resource pg_kv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: pg_kv_outputs_name
}

resource pg_kv_connectionstrings__db 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'connectionstrings--db'
  parent: pg_kv
}

resource api 'Microsoft.App/containerApps@2025-02-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--db'
          identity: api_identity_outputs_id
          keyVaultUrl: pg_kv_connectionstrings__db.properties.secretUri
        }
        {
          name: 'db-uri'
          value: 'postgresql://${uriComponent(pg_username_value)}:${uriComponent(pg_password_value)}@${pg_outputs_hostname}/db'
        }
        {
          name: 'db-password'
          value: pg_password_value
        }
        {
          name: 'secretval'
          value: value0_value
        }
        {
          name: 'secret-value-1'
          value: value0_value
        }
        {
          name: 'cs'
          value: cs_connectionstring
        }
        {
          name: 'database-url'
          value: 'postgresql://postgres:${uriComponent(pgc_password_value)}@pgc:5432'
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(api_containerport)
        transport: 'http'
        additionalPortMappings: [
          {
            external: false
            targetPort: 8000
          }
        ]
      }
      registries: [
        {
          server: env_outputs_azure_container_registry_endpoint
          identity: env_outputs_azure_container_registry_managed_identity_id
        }
      ]
      runtime: {
        dotnet: {
          autoConfigureDataProtection: true
        }
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
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
              value: '${api_containerport};8000'
            }
            {
              name: 'HTTPS_PORTS'
              value: api_containerport
            }
            {
              name: 'ConnectionStrings__mydb'
              value: mydb_outputs_connectionstring
            }
            {
              name: 'MYDB_URI'
              value: mydb_outputs_connectionstring
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
              name: 'ConnectionStrings__db'
              secretRef: 'connectionstrings--db'
            }
            {
              name: 'DB_HOST'
              value: pg_outputs_hostname
            }
            {
              name: 'DB_PORT'
              value: '5432'
            }
            {
              name: 'DB_URI'
              secretRef: 'db-uri'
            }
            {
              name: 'DB_JDBCCONNECTIONSTRING'
              value: 'jdbc:postgresql://${pg_outputs_hostname}/db?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin'
            }
            {
              name: 'DB_USERNAME'
              value: pg_username_value
            }
            {
              name: 'DB_PASSWORD'
              secretRef: 'db-password'
            }
            {
              name: 'DB_DATABASENAME'
              value: 'db'
            }
            {
              name: 'SecretVal'
              secretRef: 'secretval'
            }
            {
              name: 'secret_value_1'
              secretRef: 'secret-value-1'
            }
            {
              name: 'Value'
              value: value1_value
            }
            {
              name: 'CS'
              secretRef: 'cs'
            }
            {
              name: 'DATABASE_URL'
              secretRef: 'database-url'
            }
            {
              name: 'HTTP_EP'
              value: 'http://api.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'HTTPS_EP'
              value: 'https://api.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'INTERNAL_EP'
              value: 'http://api:8000'
            }
            {
              name: 'TARGET_PORT'
              value: api_containerport
            }
            {
              name: 'PORT'
              value: '80'
            }
            {
              name: 'HOST'
              value: 'api.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'HOSTANDPORT'
              value: 'api.internal.${env_outputs_azure_container_apps_environment_default_domain}:80'
            }
            {
              name: 'SCHEME'
              value: 'http'
            }
            {
              name: 'INTERNAL_HOSTANDPORT'
              value: 'api:8000'
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
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}
