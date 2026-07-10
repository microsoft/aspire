from aspire_app import create_builder


with create_builder() as builder:
    vnet_prefix = builder.add_parameter("vnet-prefix")
    subnet_prefix = builder.add_parameter("subnet-prefix")

    default_vnet = builder.add_azure_virtual_network("vnet-default")
    string_vnet = builder.add_azure_virtual_network("vnet-string", address_prefix="10.1.0.0/16")
    parameter_vnet = builder.add_azure_virtual_network("vnet-parameter", address_prefix=vnet_prefix)

    default_vnet.add_subnet("default-subnet", "10.0.1.0/24")
    string_vnet.add_subnet("string-subnet", "10.1.1.0/24", subnet_name="string-subnet-name")
    parameter_vnet.add_subnet("parameter-subnet", subnet_prefix, subnet_name="parameter-subnet-name")

    delegation_vnet = builder.add_azure_virtual_network("vnet-delegation", address_prefix="10.2.0.0/16")

    aci_subnet = delegation_vnet.add_subnet("aci-subnet", "10.2.0.0/23")
    aci_subnet.with_container_instance_delegation()

    app_env_subnet = delegation_vnet.add_subnet("app-subnet", "10.2.2.0/23")
    app_env_subnet.with_service_delegation("Microsoft.App/environments")

    named_delegation_subnet = delegation_vnet.add_subnet("named-subnet", "10.2.4.0/23")
    named_delegation_subnet.with_service_delegation("Microsoft.App/environments", name="app-delegation")

    perimeter = builder.add_network_security_perimeter("data-boundary")
    perimeter.with_access_rule({
        "Name": "allow-corp-network",
        "Direction": "Inbound",
        "AddressPrefixes": ["203.0.113.0/24"],
    })

    builder.run()
