import {
    createBuilder,
    NetworkSecurityPerimeterAccessRuleDirection
} from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const vnetPrefix = await builder.addParameter('vnet-prefix');
const subnetPrefix = await builder.addParameter('subnet-prefix');

const defaultVnet = await builder.addAzureVirtualNetwork('vnet-default');
const stringVnet = await builder.addAzureVirtualNetwork('vnet-string', { addressPrefix: '10.1.0.0/16' });
const parameterVnet = await builder.addAzureVirtualNetwork('vnet-parameter', { addressPrefix: vnetPrefix });

await defaultVnet.addSubnet('default-subnet', '10.0.1.0/24');
await stringVnet.addSubnet('string-subnet', '10.1.1.0/24', { subnetName: 'string-subnet-name' });
await parameterVnet.addSubnet('parameter-subnet', subnetPrefix, { subnetName: 'parameter-subnet-name' });

const delegationVnet = await builder.addAzureVirtualNetwork('vnet-delegation', { addressPrefix: '10.2.0.0/16' });

const aciSubnet = await delegationVnet.addSubnet('aci-subnet', '10.2.0.0/23');
await aciSubnet.withContainerInstanceDelegation();

const appEnvSubnet = await delegationVnet.addSubnet('app-subnet', '10.2.2.0/23');
await appEnvSubnet.withServiceDelegation('Microsoft.App/environments');

const namedDelegationSubnet = await delegationVnet.addSubnet('named-subnet', '10.2.4.0/23');
await namedDelegationSubnet.withServiceDelegation('Microsoft.App/environments', { name: 'app-delegation' });

const perimeter = await builder.addNetworkSecurityPerimeter('data-boundary');
await perimeter.withAccessRule({
    name: 'allow-corp-network',
    direction: NetworkSecurityPerimeterAccessRuleDirection.Inbound,
    addressPrefixes: ['203.0.113.0/24']
});

await builder.build().run();
