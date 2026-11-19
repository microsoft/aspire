@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

param servicea_containerimage string

param servicea_identity_outputs_id string

param servicea_containerport string

param wps1_outputs_endpoint string

param servicea_identity_outputs_clientid string

resource serviceA 'Microsoft.App/containerApps@2026-03-02-preview' = {
  name: 'servicea'
  location: location
  properties: {
    environmentId: env_outputs_azure_container_apps_environment_id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(servicea_containerport)
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
    template: {
      containers: [
        {
          image: servicea_containerimage
          name: 'servicea'
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
              name: 'HTTPS_PORTS'
              value: servicea_containerport
            }
            {
              name: 'ConnectionStrings__abc'
              value: 'Endpoint=${wps1_outputs_endpoint};Hub=abc'
            }
            {
              name: 'ABC_URI'
              value: wps1_outputs_endpoint
            }
            {
              name: 'ABC_HUBNAME'
              value: 'abc'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: servicea_identity_outputs_clientid
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
      '${servicea_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}

output https_url string = 'https://${serviceA.properties.configuration.ingress.fqdn}'