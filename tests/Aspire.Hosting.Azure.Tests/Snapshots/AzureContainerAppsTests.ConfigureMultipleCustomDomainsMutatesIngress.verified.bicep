@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param certificateName1 string

param customDomain1 string

param certificateName2 string

param customDomain2 string

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
            name: customDomain1
            bindingType: (certificateName1 != '') ? 'SniEnabled' : 'Disabled'
            certificateId: (certificateName1 != '') ? '${env_outputs_azure_container_apps_environment_id}/managedCertificates/${certificateName1}' : null
          }
          {
            name: customDomain2
            bindingType: (certificateName2 != '') ? 'SniEnabled' : 'Disabled'
            certificateId: (certificateName2 != '') ? '${env_outputs_azure_container_apps_environment_id}/managedCertificates/${certificateName2}' : null
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