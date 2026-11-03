import {
    createBuilder,
    NetworkSecurityPerimeterAccessRuleDirection
} from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const vnetPrefix = await builder.addParameter('vnet-prefix');
const subnetPrefix = await builder.addParameter('subnet-prefix');

const defaultVnet = await builder.addAzureVirtualNetwork('vnet-default');
await defaultVnet.configureInfrastructure(async infrastructure => {
    const network = await infrastructure.getVirtualNetwork();
    await network.flowTimeoutInMinutes.set(10);
    const _flowTimeout = await network.flowTimeoutInMinutes.get();
    const serviceEndpoint = await infrastructure.createServiceEndpointProperties();
    await serviceEndpoint.service.set("Microsoft.Storage");
    const locations = await serviceEndpoint.locations.get();
    await locations.add(await infrastructure.createNetworkAzureLocation("westus2"));
    const location = await locations.get(0);
    const _locationName = await location.name();
});

const nsg = await builder.addNetworkSecurityGroup("web-nsg");
await nsg.configureInfrastructure(async infrastructure => {
    const group = await infrastructure.getNetworkSecurityGroup();
    const tags = await group.tags.get();
    await tags.set("provisioning-proxy", "typescript");
    const _tag = await tags.get("provisioning-proxy");
});
const publicIp = await builder.addPublicIPAddress("nat-ip");
await publicIp.configureInfrastructure(async infrastructure => {
    const address = await infrastructure.getPublicIPAddress();
    await address.idleTimeoutInMinutes.set(10);
    const _idleTimeout = await address.idleTimeoutInMinutes.get();
});
const nat = await builder.addNatGateway("nat");
await nat.withPublicIPAddress(publicIp);
await nat.configureInfrastructure(async infrastructure => {
    const gateway = await infrastructure.getNatGateway();
    await gateway.idleTimeoutInMinutes.set(10);
    const _idleTimeout = await gateway.idleTimeoutInMinutes.get();
});

const privateSubnet = await defaultVnet.addSubnet("private-subnet", "10.0.2.0/24");
const storage = await builder.addAzureStorage("private-storage");
const blobs = await storage.addBlobs("private-blobs");
const privateEndpoint = await privateSubnet.addPrivateEndpoint(blobs);
await privateEndpoint.configureInfrastructure(async infrastructure => {
    const endpoint = await infrastructure.getPrivateEndpoint();
    await endpoint.customNetworkInterfaceName.set("private-blobs-nic");
    const _interfaceName = await endpoint.customNetworkInterfaceName.get();
});

// Hosting creates Private DNS zones internally; use a standalone zone to exercise the SDK.
const dns = await builder.addAzureInfrastructure("privateDns", async infrastructure => {
    const zone = await infrastructure.addPrivateDnsZone("privateDns");
    await zone.location.set(infrastructure.bicep().location("global"));
});
await dns.configureInfrastructure(async infrastructure => {
    const zone = await infrastructure.getPrivateDnsZone();
    await zone.name.set("polyglot.internal");
    const _zoneName = await zone.name.get();
});
const stringVnet = await builder.addAzureVirtualNetwork('vnet-string', { addressPrefix: '10.1.0.0/16' });
const parameterVnet = await builder.addAzureVirtualNetwork('vnet-parameter', { addressPrefix: vnetPrefix });

await defaultVnet.addSubnet('default-subnet', '10.0.1.0/24');
await stringVnet.addSubnet('string-subnet', '10.1.1.0/24', { subnetName: 'string-subnet-name' });
await parameterVnet.addSubnet('parameter-subnet', subnetPrefix, { subnetName: 'parameter-subnet-name' });

const delegationVnet = await builder.addAzureVirtualNetwork('vnet-delegation', { addressPrefix: '10.2.0.0/16' });

const aciSubnet = await delegationVnet.addSubnet('aci-subnet', '10.2.0.0/23');
await aciSubnet.withServiceDelegation('Microsoft.ContainerInstance/containerGroups');

const appEnvSubnet = await delegationVnet.addSubnet('app-subnet', '10.2.2.0/23');
await appEnvSubnet.withServiceDelegation('Microsoft.App/environments');

const namedDelegationSubnet = await delegationVnet.addSubnet('named-subnet', '10.2.4.0/23');
await namedDelegationSubnet.withServiceDelegation('Microsoft.App/environments', { name: 'app-delegation' });

const perimeter = await builder.addNetworkSecurityPerimeter('data-boundary');
await perimeter.configureInfrastructure(async infrastructure => {
    const boundary = await infrastructure.getNetworkSecurityPerimeter();
    const tags = await boundary.tags.get();
    await tags.set("provisioning-proxy", "typescript");
    const _tag = await tags.get("provisioning-proxy");
});
await perimeter.withAccessRule({
    name: 'allow-corp-network',
    direction: NetworkSecurityPerimeterAccessRuleDirection.Inbound,
    addressPrefixes: ['203.0.113.0/24']
});

await builder.build().run();
