// Generated offline from portable provider metadata. Do not edit.
import { type AspireClientRpc, type MarshalledHandle, registerHandleWrapper, wrapIfHandle } from './transport.mjs';
import type { Resource } from './aspire.mjs';
export class IntegrationClient {
    constructor(private readonly client: AspireClientRpc) {}
    async getName(resource: Resource): Promise<string> {
        const result = await this.client.invokeCapability("example/getName", {
            resource,
        });
        return wrapIfHandle(result, this.client) as string;
    }
}
