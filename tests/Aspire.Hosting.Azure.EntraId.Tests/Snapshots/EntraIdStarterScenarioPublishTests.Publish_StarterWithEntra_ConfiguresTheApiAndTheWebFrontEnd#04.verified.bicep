@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

param webfrontend_containerimage string

param webfrontend_identity_outputs_id string

param webfrontend_containerport string

param webfrontend_entra_outputs_tenantid string

param webfrontend_entra_outputs_clientid string

param webfrontend_identity_outputs_clientid string

param apiservice_entra_outputs_applicationiduri string

resource webfrontend 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'webfrontend'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(webfrontend_containerport)
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
          image: webfrontend_containerimage
          name: 'webfrontend'
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
              value: webfrontend_containerport
            }
            {
              name: 'AzureAd__Instance'
              value: 'https://login.microsoftonline.com/'
            }
            {
              name: 'AzureAd__TenantId'
              value: webfrontend_entra_outputs_tenantid
            }
            {
              name: 'AzureAd__ClientId'
              value: webfrontend_entra_outputs_clientid
            }
            {
              name: 'AzureAd__ClientCredentials__0__SourceType'
              value: 'SignedAssertionFromManagedIdentity'
            }
            {
              name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId'
              value: webfrontend_identity_outputs_clientid
            }
            {
              name: 'MyDownstreamApis__apiservice__Scopes__0'
              value: '${apiservice_entra_outputs_applicationiduri}/access_as_user'
            }
            {
              name: 'APISERVICE_HTTP'
              value: 'https://apiservice.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__apiservice__http__0'
              value: 'https://apiservice.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'APISERVICE_HTTPS'
              value: 'https://apiservice.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__apiservice__https__0'
              value: 'https://apiservice.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: webfrontend_identity_outputs_clientid
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
      '${webfrontend_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}