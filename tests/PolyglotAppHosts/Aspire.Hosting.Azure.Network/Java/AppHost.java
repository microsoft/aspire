import aspire.*;

void main() throws Exception {
    var builder = DistributedApplication.CreateBuilder();

    var vnetPrefix = builder.addParameter("vnet-prefix");
    var subnetPrefix = builder.addParameter("subnet-prefix");

    var defaultVnet = builder.addAzureVirtualNetwork("vnet-default");
    var stringVnet = builder.addAzureVirtualNetwork("vnet-string", "10.1.0.0/16");
    var parameterVnet = builder.addAzureVirtualNetwork("vnet-parameter", vnetPrefix);

    defaultVnet.addSubnet("default-subnet", "10.0.1.0/24", null);
    stringVnet.addSubnet("string-subnet", "10.1.1.0/24", "string-subnet-name");
    parameterVnet.addSubnet("parameter-subnet", subnetPrefix, "parameter-subnet-name");

    var perimeter = builder.addNetworkSecurityPerimeter("data-boundary");
    var accessRule = new AzureNspAccessRule();
    accessRule.setName("allow-corp-network");
    accessRule.setDirection(NetworkSecurityPerimeterAccessRuleDirection.INBOUND);
    accessRule.setAddressPrefixes(java.util.List.of("203.0.113.0/24"));
    perimeter.withAccessRule(accessRule);

    builder.build().run();
}
