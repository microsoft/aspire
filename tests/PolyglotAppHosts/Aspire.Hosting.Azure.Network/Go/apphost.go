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
	aciSubnet.WithContainerInstanceDelegation()

	appEnvSubnet := delegationVnet.AddSubnet("app-subnet", "10.2.2.0/23", nil)
	appEnvSubnet.WithServiceDelegation("Microsoft.App/environments", nil)

	namedDelegationSubnet := delegationVnet.AddSubnet("named-subnet", "10.2.4.0/23", nil)
	namedDelegationSubnet.WithServiceDelegation("Microsoft.App/environments", &aspire.WithServiceDelegationOptions{
		Name: aspire.StringPtr("app-delegation"),
	})

	perimeter := builder.AddNetworkSecurityPerimeter("data-boundary")
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
