@description('The location for the resources to be deployed.')
param location string = resourceGroup().location

param resourceName string
param vmSku string
param capacity string
param imagePublisher string
param imageOffer string
param imageSku string
param imageVersion string
param subnetId string
@secure()
param vmApplicationPackageUri string
param vmApplicationVersion string
param adminSshPublicKey string
param workloadIdentityName string
param galleryName string
param galleryApplicationName string
param adminUsername string = 'azureuser'
param tags object = {}

var suffix = uniqueString(resourceGroup().id)

resource workloadIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: workloadIdentityName
}

resource gallery 'Microsoft.Compute/galleries@2025-03-03' existing = {
  name: galleryName
}

resource galleryApplication 'Microsoft.Compute/galleries/applications@2025-03-03' existing = {
  parent: gallery
  name: galleryApplicationName
}

resource galleryApplicationVersion 'Microsoft.Compute/galleries/applications/versions@2025-03-03' = {
  parent: galleryApplication
  name: vmApplicationVersion
  location: location
  properties: {
    publishingProfile: {
      source: {
        mediaLink: vmApplicationPackageUri
      }
      manageActions: {
        install: 'staging="$(mktemp -d)" && trap \'rm -rf "$staging"\' EXIT && tar -xzf aspire-application.tar.gz --no-same-owner -C "$staging" && bash "$staging/install.sh"'
        update: 'staging="$(mktemp -d)" && trap \'rm -rf "$staging"\' EXIT && tar -xzf aspire-application.tar.gz --no-same-owner -C "$staging" && bash "$staging/update.sh"'
        remove: 'if [ -f /opt/aspire/${resourceName}/remove.sh ]; then bash /opt/aspire/${resourceName}/remove.sh || exit $?; fi; rm -rf /opt/aspire/${resourceName}'
      }
      settings: {
        packageFileName: 'aspire-application.tar.gz'
      }
      targetRegions: [
        {
          name: location
          regionalReplicaCount: 1
          storageAccountType: 'Standard_LRS'
        }
      ]
      replicaCount: 1
      excludeFromLatest: false
    }
    safetyProfile: {
      allowDeletionOfReplicatedLocations: false
    }
  }
}

resource vmss 'Microsoft.Compute/virtualMachineScaleSets@2025-04-01' = {
  name: take('${resourceName}-${suffix}', 64)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workloadIdentity.id}': {}
    }
  }
  sku: {
    name: vmSku
    tier: 'Standard'
    capacity: int(capacity)
  }
  properties: {
    // Uniform orchestration applies VMSS model updates according to upgradePolicy. Flexible
    // orchestration requires an explicit per-instance update that this preview does not perform.
    // See https://learn.microsoft.com/azure/virtual-machine-scale-sets/virtual-machine-scale-sets-orchestration-modes.
    orchestrationMode: 'Uniform'
    platformFaultDomainCount: 1
    singlePlacementGroup: false
    zoneBalance: false
    upgradePolicy: {
      // Rolling upgrades require an application health contract, which this preview does not expose yet.
      // Automatic mode ensures a newly pinned VM Application version reaches existing instances.
      mode: 'Automatic'
    }
    virtualMachineProfile: {
      osProfile: {
        computerNamePrefix: 'aspire'
        adminUsername: adminUsername
        linuxConfiguration: {
          disablePasswordAuthentication: true
          provisionVMAgent: true
          ssh: {
            publicKeys: [
              {
                path: '/home/${adminUsername}/.ssh/authorized_keys'
                keyData: adminSshPublicKey
              }
            ]
          }
        }
      }
      storageProfile: {
        imageReference: {
          publisher: imagePublisher
          offer: imageOffer
          sku: imageSku
          version: imageVersion
        }
        osDisk: {
          createOption: 'FromImage'
          caching: 'ReadWrite'
          managedDisk: {
            storageAccountType: 'Standard_LRS'
          }
        }
      }
      networkProfile: {
        networkInterfaceConfigurations: [
          {
            name: 'primary'
            properties: {
              primary: true
              ipConfigurations: [
                {
                  name: 'primary'
                  properties: {
                    primary: true
                    subnet: {
                      id: subnetId
                    }
                  }
                }
              ]
            }
          }
        ]
      }
      applicationProfile: {
        galleryApplications: [
          {
            packageReferenceId: galleryApplicationVersion.id
            treatFailureAsDeploymentFailure: true
            enableAutomaticUpgrade: false
            order: 1
          }
        ]
      }
    }
  }
  tags: tags
}

output vmssId string = vmss.id
output workloadIdentityClientId string = workloadIdentity.properties.clientId
output galleryApplicationVersionId string = galleryApplicationVersion.id
