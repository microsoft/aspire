import {
    createBuilder,
    NetworkSecurityPerimeterAccessRuleDirection
} from './.modules/aspire.mjs';

const builder = await createBuilder();

const vnetPrefix = await builder.addParameter('vnet-prefix');
const subnetPrefix = await builder.addParameter('subnet-prefix');

const defaultVnet = await builder.addAzureVirtualNetwork('vnet-default');
const stringVnet = await builder.addAzureVirtualNetwork('vnet-string', { addressPrefix: '10.1.0.0/16' });
const parameterVnet = await builder.addAzureVirtualNetwork('vnet-parameter', { addressPrefix: vnetPrefix });

await defaultVnet.addSubnet('default-subnet', '10.0.1.0/24');
await stringVnet.addSubnet('string-subnet', '10.1.1.0/24', { subnetName: 'string-subnet-name' });
await parameterVnet.addSubnet('parameter-subnet', subnetPrefix, { subnetName: 'parameter-subnet-name' });

const perimeter = await builder.addNetworkSecurityPerimeter('data-boundary');
await perimeter.withAccessRule({
    name: 'allow-corp-network',
    direction: NetworkSecurityPerimeterAccessRuleDirection.Inbound,
    addressPrefixes: ['203.0.113.0/24']
});

await builder.build().run();
