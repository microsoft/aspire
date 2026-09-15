package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	vnetPrefix := builder.AddParameter("vnet-prefix", nil)
	subnetPrefix := builder.AddParameter("subnet-prefix", nil)

	defaultVnet := builder.AddAzureVirtualNetwork("vnet-default", nil)
	defaultVnet.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		network := infrastructure.GetVirtualNetwork()
		if err := network.SetFlowTimeoutInMinutes(float64(10)).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := network.FlowTimeoutInMinutes(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		serviceEndpoint := infrastructure.CreateServiceEndpointProperties()
		if err := serviceEndpoint.SetService("Microsoft.Storage").Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := serviceEndpoint.Locations().Add(infrastructure.CreateNetworkAzureLocation("westus2")); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		location := serviceEndpoint.Locations().Get(0)
		if _, err := location.Name(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	nsg := builder.AddNetworkSecurityGroup("web-nsg")
	nsg.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		group := infrastructure.GetNetworkSecurityGroup()
		if err := group.Tags().Set("provisioning-proxy", "go"); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := group.Tags().Get("provisioning-proxy").Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	publicIp := builder.AddPublicIPAddress("nat-ip")
	publicIp.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		address := infrastructure.GetPublicIPAddress()
		if err := address.SetIdleTimeoutInMinutes(float64(10)).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := address.IdleTimeoutInMinutes(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	nat := builder.AddNatGateway("nat")
	nat.WithPublicIPAddress(publicIp)
	nat.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		gateway := infrastructure.GetNatGateway()
		if err := gateway.SetIdleTimeoutInMinutes(float64(10)).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := gateway.IdleTimeoutInMinutes(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	privateSubnet := defaultVnet.AddSubnet("private-subnet", "10.0.2.0/24")
	storage := builder.AddAzureStorage("private-storage")
	blobs := storage.AddBlobs("private-blobs")
	privateEndpoint := privateSubnet.AddPrivateEndpoint(blobs)
	privateEndpoint.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		endpoint := infrastructure.GetPrivateEndpoint()
		if err := endpoint.SetCustomNetworkInterfaceName("private-blobs-nic").Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := endpoint.CustomNetworkInterfaceName(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	// Hosting creates Private DNS zones internally, without an exported factory.
	dns := builder.AddAzureInfrastructure("privateDns", func(infrastructure aspire.AzureResourceInfrastructure) {
		zone := infrastructure.AddPrivateDnsZone("privateDns")
		if err := zone.SetLocation(infrastructure.Bicep().Location("global")).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	dns.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		zone := infrastructure.GetPrivateDnsZone()
		if err := zone.SetName("polyglot.internal").Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := zone.Name(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	for _, err := range []error{defaultVnet.Err(), nsg.Err(), publicIp.Err(), nat.Err(), privateEndpoint.Err(), dns.Err()} {
		if err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	}
	stringVnet := builder.AddAzureVirtualNetwork("vnet-string", &aspire.AddAzureVirtualNetworkOptions{
		AddressPrefix: aspire.StringPtr("10.1.0.0/16"),
	})
	parameterVnet := builder.AddAzureVirtualNetwork("vnet-parameter", &aspire.AddAzureVirtualNetworkOptions{
		AddressPrefix: vnetPrefix,
	})

	defaultVnet.AddSubnet("default-subnet", "10.0.1.0/24", nil)
	stringVnet.AddSubnet("string-subnet", "10.1.1.0/24", &aspire.AddSubnetOptions{
		SubnetName: aspire.StringPtr("string-subnet-name"),
	})
	parameterVnet.AddSubnet("parameter-subnet", subnetPrefix, &aspire.AddSubnetOptions{
		SubnetName: aspire.StringPtr("parameter-subnet-name"),
	})

	delegationVnet := builder.AddAzureVirtualNetwork("vnet-delegation", &aspire.AddAzureVirtualNetworkOptions{
		AddressPrefix: aspire.StringPtr("10.2.0.0/16"),
	})

	aciSubnet := delegationVnet.AddSubnet("aci-subnet", "10.2.0.0/23", nil)
	aciSubnet.WithServiceDelegation("Microsoft.ContainerInstance/containerGroups", nil)

	appEnvSubnet := delegationVnet.AddSubnet("app-subnet", "10.2.2.0/23", nil)
	appEnvSubnet.WithServiceDelegation("Microsoft.App/environments", nil)

	namedDelegationSubnet := delegationVnet.AddSubnet("named-subnet", "10.2.4.0/23", nil)
	namedDelegationSubnet.WithServiceDelegation("Microsoft.App/environments", &aspire.WithServiceDelegationOptions{
		Name: aspire.StringPtr("app-delegation"),
	})

	perimeter := builder.AddNetworkSecurityPerimeter("data-boundary")
	perimeter.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		boundary := infrastructure.GetNetworkSecurityPerimeter()
		if err := boundary.Tags().Set("provisioning-proxy", "go"); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := boundary.Tags().Get("provisioning-proxy").Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	if perimeter.Err() != nil {
		log.Fatalf(aspire.FormatError(perimeter.Err()))
	}
	perimeter.WithAccessRule(&aspire.AzureNspAccessRule{
		Name:            "allow-corp-network",
		Direction:       aspire.NetworkSecurityPerimeterAccessRuleDirectionInbound,
		AddressPrefixes: []string{"203.0.113.0/24"},
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
