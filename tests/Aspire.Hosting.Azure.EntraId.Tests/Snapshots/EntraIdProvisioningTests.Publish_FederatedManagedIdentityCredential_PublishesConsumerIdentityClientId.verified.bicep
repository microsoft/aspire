@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param web_identity_outputs_id string

param entra_web_outputs_tenantid string

param entra_web_outputs_clientid string

param web_identity_outputs_clientid string

resource web 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'web'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'image:latest'
          name: 'web'
          env: [
            {
              name: 'ENTRA_WEB_INSTANCE'
              value: 'https://login.microsoftonline.com/'
            }
            {
              name: 'ENTRA_WEB_TENANT_ID'
              value: entra_web_outputs_tenantid
            }
            {
              name: 'ENTRA_WEB_CLIENT_ID'
              value: entra_web_outputs_clientid
            }
            {
              name: 'ENTRA_WEB_CLIENT_CREDENTIALS_0_SOURCE_TYPE'
              value: 'SignedAssertionFromManagedIdentity'
            }
            {
              name: 'ENTRA_WEB_CLIENT_CREDENTIALS_0_MANAGED_IDENTITY_CLIENT_ID'
              value: web_identity_outputs_clientid
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: web_identity_outputs_clientid
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
      '${web_identity_outputs_id}': { }
    }
  }
}