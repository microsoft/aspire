@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param api_containerimage string

param api_identity_outputs_id string

param api_containerport string

param sql_outputs_sqlserverfqdn string

param storage_outputs_blobendpoint string

param storage_outputs_queueendpoint string

param api_identity_outputs_clientid string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource api 'Microsoft.App/containerApps@2025-02-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(api_containerport)
        transport: 'http'
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
              value: api_containerport
            }
            {
              name: 'ConnectionStrings__sqldb'
              value: 'Server=tcp:${sql_outputs_sqlserverfqdn},1433;Encrypt=True;Authentication="Active Directory Default";Database=sqldb'
            }
            {
              name: 'SQLDB_HOST'
              value: sql_outputs_sqlserverfqdn
            }
            {
              name: 'SQLDB_PORT'
              value: '1433'
            }
            {
              name: 'SQLDB_URI'
              value: 'mssql://${sql_outputs_sqlserverfqdn}:1433/sqldb'
            }
            {
              name: 'SQLDB_JDBCCONNECTIONSTRING'
              value: 'jdbc:sqlserver://${sql_outputs_sqlserverfqdn}:1433;database=sqldb;encrypt=true;trustServerCertificate=false'
            }
            {
              name: 'SQLDB_DATABASENAME'
              value: 'sqldb'
            }
            {
              name: 'ConnectionStrings__mycontainer'
              value: 'Endpoint=${storage_outputs_blobendpoint};ContainerName=mycontainer'
            }
            {
              name: 'MYCONTAINER_URI'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'MYCONTAINER_BLOBCONTAINERNAME'
              value: 'mycontainer'
            }
            {
              name: 'ConnectionStrings__myqueue'
              value: 'Endpoint=${storage_outputs_queueendpoint};QueueName=myqueue'
            }
            {
              name: 'MYQUEUE_URI'
              value: storage_outputs_queueendpoint
            }
            {
              name: 'MYQUEUE_QUEUENAME'
              value: 'myqueue'
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