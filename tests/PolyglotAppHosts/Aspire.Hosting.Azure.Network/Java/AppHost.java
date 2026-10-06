import aspire.*;

void main() throws Exception {
    var aad = VpnAuthenticationType.fromValue("AAD");
    var aadMixedCase = VpnAuthenticationType.fromValue("Aad");
    if (aad == aadMixedCase || !"AAD".equals(aad.getValue()) || !"Aad".equals(aadMixedCase.getValue())) {
        throw new IllegalStateException("VPN authentication types must preserve distinct wire values.");
    }
    var builder = DistributedApplication.CreateBuilder();

    var vnetPrefix = builder.addParameter("vnet-prefix");
    var subnetPrefix = builder.addParameter("subnet-prefix");

    var defaultVnet = builder.addAzureVirtualNetwork("vnet-default");
    defaultVnet.configureInfrastructure(infrastructure -> {
        var network = infrastructure.getVirtualNetwork();
        network.setFlowTimeoutInMinutes(10);
        var _flowTimeout = network.flowTimeoutInMinutes();
        var serviceEndpoint = infrastructure.createServiceEndpointProperties();
        serviceEndpoint.setService("Microsoft.Storage");
        serviceEndpoint.locations().add(infrastructure.createNetworkAzureLocation("westus2"));
        var location = serviceEndpoint.locations().get(0);
        var _locationName = location.name();
    });
    var nsg = builder.addNetworkSecurityGroup("web-nsg");
    nsg.configureInfrastructure(infrastructure -> {
        var group = infrastructure.getNetworkSecurityGroup();
        group.tags().set("provisioning-proxy", "java");
        var _tag = group.tags().get("provisioning-proxy");
    });
    var publicIp = builder.addPublicIPAddress("nat-ip");
    publicIp.configureInfrastructure(infrastructure -> {
        var address = infrastructure.getPublicIPAddress();
        address.setIdleTimeoutInMinutes(10);
        var _idleTimeout = address.idleTimeoutInMinutes();
    });
    var nat = builder.addNatGateway("nat");
    nat.withPublicIPAddress(publicIp);
    nat.configureInfrastructure(infrastructure -> {
        var gateway = infrastructure.getNatGateway();
        gateway.setIdleTimeoutInMinutes(10);
        var _idleTimeout = gateway.idleTimeoutInMinutes();
    });
    var privateSubnet = defaultVnet.addSubnet("private-subnet", "10.0.2.0/24", null);
    var storage = builder.addAzureStorage("private-storage");
    var blobs = storage.addBlobs("private-blobs");
    var privateEndpoint = privateSubnet.addPrivateEndpoint(blobs);
    privateEndpoint.configureInfrastructure(infrastructure -> {
        var endpoint = infrastructure.getPrivateEndpoint();
        endpoint.setCustomNetworkInterfaceName("private-blobs-nic");
        var _interfaceName = endpoint.customNetworkInterfaceName();
    });
    // Hosting creates Private DNS zones internally, without an exported factory.
    var dns = builder.addAzureInfrastructure("privateDns", infrastructure -> {
        var zone = infrastructure.addPrivateDnsZone("privateDns");
        zone.setLocation(infrastructure.bicep().location("global"));
    });
    dns.configureInfrastructure(infrastructure -> {
        var zone = infrastructure.getPrivateDnsZone();
        zone.setName("polyglot.internal");
        var _zoneName = zone.name();
    });
    var stringVnet = builder.addAzureVirtualNetwork("vnet-string", "10.1.0.0/16");
    var parameterVnet = builder.addAzureVirtualNetwork("vnet-parameter", vnetPrefix);

    defaultVnet.addSubnet("default-subnet", "10.0.1.0/24", null);
    stringVnet.addSubnet("string-subnet", "10.1.1.0/24", "string-subnet-name");
    parameterVnet.addSubnet("parameter-subnet", subnetPrefix, "parameter-subnet-name");

    var delegationVnet = builder.addAzureVirtualNetwork("vnet-delegation", "10.2.0.0/16");

    var aciSubnet = delegationVnet.addSubnet("aci-subnet", "10.2.0.0/23", null);
    aciSubnet.withServiceDelegation("Microsoft.ContainerInstance/containerGroups", null);

    var appEnvSubnet = delegationVnet.addSubnet("app-subnet", "10.2.2.0/23", null);
    appEnvSubnet.withServiceDelegation("Microsoft.App/environments", null);

    var namedDelegationSubnet = delegationVnet.addSubnet("named-subnet", "10.2.4.0/23", null);
    namedDelegationSubnet.withServiceDelegation("Microsoft.App/environments", "app-delegation");

    var perimeter = builder.addNetworkSecurityPerimeter("data-boundary");
    perimeter.configureInfrastructure(infrastructure -> {
        var boundary = infrastructure.getNetworkSecurityPerimeter();
        boundary.tags().set("provisioning-proxy", "java");
        var _tag = boundary.tags().get("provisioning-proxy");
    });
    var accessRule = new AzureNspAccessRule();
    accessRule.setName("allow-corp-network");
    accessRule.setDirection(NetworkSecurityPerimeterAccessRuleDirection.INBOUND);
    accessRule.setAddressPrefixes(java.util.List.of("203.0.113.0/24"));
    perimeter.withAccessRule(accessRule);

    builder.build().run();
}
