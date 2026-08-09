@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param initialCertificateName string

param customDomain string

param expectedCertificateName string

resource api 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 1111
        transport: 'http'
        customDomains: [
          {
            name: customDomain
            bindingType: (expectedCertificateName != '') ? 'SniEnabled' : 'Disabled'
            certificateId: (expectedCertificateName != '') ? '${env_outputs_azure_container_apps_environment_id}/managedCertificates/${expectedCertificateName}' : null
          }
        ]
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'api'
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
}