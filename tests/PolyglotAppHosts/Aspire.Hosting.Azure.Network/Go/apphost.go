package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
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

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
