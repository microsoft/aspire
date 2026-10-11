from aspire_app import AzureResourceInfrastructure, create_builder


def configure_network(infrastructure: AzureResourceInfrastructure) -> None:
    network = infrastructure.get_virtual_network()
    network.flow_timeout_in_minutes = 10
    _flow_timeout = network.flow_timeout_in_minutes
    service_endpoint = infrastructure.create_service_endpoint_properties()
    service_endpoint.service = "Microsoft.Storage"
    service_endpoint.locations.add(infrastructure.create_network_azure_location("westus2"))
    location = service_endpoint.locations.get(0)
    _location_name = location.name


def configure_security_group(infrastructure: AzureResourceInfrastructure) -> None:
    group = infrastructure.get_network_security_group()
    group.tags.set("provisioning-proxy", "python")
    _tag = group.tags.get("provisioning-proxy")


def configure_public_ip(infrastructure: AzureResourceInfrastructure) -> None:
    address = infrastructure.get_public_ip_address()
    address.idle_timeout_in_minutes = 10
    _idle_timeout = address.idle_timeout_in_minutes


def configure_nat(infrastructure: AzureResourceInfrastructure) -> None:
    gateway = infrastructure.get_nat_gateway()
    gateway.idle_timeout_in_minutes = 10
    _idle_timeout = gateway.idle_timeout_in_minutes


def configure_private_endpoint(infrastructure: AzureResourceInfrastructure) -> None:
    endpoint = infrastructure.get_private_endpoint()
    endpoint.custom_network_interface_name = "private-blobs-nic"
    _interface_name = endpoint.custom_network_interface_name


def create_dns(infrastructure: AzureResourceInfrastructure) -> None:
    # Hosting creates Private DNS zones internally, without an exported factory.
    zone = infrastructure.add_private_dns_zone("privateDns")
    zone.location = infrastructure.bicep().location("global")


def configure_dns(infrastructure: AzureResourceInfrastructure) -> None:
    zone = infrastructure.get_private_dns_zone()
    zone.name = "polyglot.internal"
    _zone_name = zone.name


def configure_perimeter(infrastructure: AzureResourceInfrastructure) -> None:
    boundary = infrastructure.get_network_security_perimeter()
    boundary.tags.set("provisioning-proxy", "python")
    _tag = boundary.tags.get("provisioning-proxy")


with create_builder() as builder:
    vnet_prefix = builder.add_parameter("vnet-prefix")
    subnet_prefix = builder.add_parameter("subnet-prefix")

    default_vnet = builder.add_azure_virtual_network("vnet-default")
    default_vnet.configure_infrastructure(configure_network)
    nsg = builder.add_network_security_group("web-nsg")
    nsg.configure_infrastructure(configure_security_group)
    public_ip = builder.add_public_ip_address("nat-ip")
    public_ip.configure_infrastructure(configure_public_ip)
    nat = builder.add_nat_gateway("nat")
    nat.with_public_ip_address(public_ip)
    nat.configure_infrastructure(configure_nat)
    private_subnet = default_vnet.add_subnet("private-subnet", "10.0.2.0/24")
    storage = builder.add_azure_storage("private-storage")
    blobs = storage.add_blobs("private-blobs")
    private_endpoint = private_subnet.add_private_endpoint(blobs)
    private_endpoint.configure_infrastructure(configure_private_endpoint)
    dns = builder.add_azure_infrastructure("privateDns", create_dns)
    dns.configure_infrastructure(configure_dns)
    string_vnet = builder.add_azure_virtual_network("vnet-string", address_prefix="10.1.0.0/16")
    parameter_vnet = builder.add_azure_virtual_network("vnet-parameter", address_prefix=vnet_prefix)

    default_vnet.add_subnet("default-subnet", "10.0.1.0/24")
    string_vnet.add_subnet("string-subnet", "10.1.1.0/24", subnet_name="string-subnet-name")
    parameter_vnet.add_subnet("parameter-subnet", subnet_prefix, subnet_name="parameter-subnet-name")

    delegation_vnet = builder.add_azure_virtual_network("vnet-delegation", address_prefix="10.2.0.0/16")

    aci_subnet = delegation_vnet.add_subnet("aci-subnet", "10.2.0.0/23")
    aci_subnet.with_service_delegation("Microsoft.ContainerInstance/containerGroups")

    app_env_subnet = delegation_vnet.add_subnet("app-subnet", "10.2.2.0/23")
    app_env_subnet.with_service_delegation("Microsoft.App/environments")

    named_delegation_subnet = delegation_vnet.add_subnet("named-subnet", "10.2.4.0/23")
    named_delegation_subnet.with_service_delegation("Microsoft.App/environments", name="app-delegation")

    perimeter = builder.add_network_security_perimeter("data-boundary")
    perimeter.configure_infrastructure(configure_perimeter)
    perimeter.with_access_rule({
        "Name": "allow-corp-network",
        "Direction": "Inbound",
        "AddressPrefixes": ["203.0.113.0/24"],
    })

    builder.run()
