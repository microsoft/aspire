// Generated offline from portable provider metadata. Do not edit.
import { type AspireClientRpc, type MarshalledHandle, registerHandleWrapper, wrapIfHandle } from './transport.mjs';
export class IntegrationClient {
    constructor(private readonly client: AspireClientRpc) {}
    async greet(name: string): Promise<string> {
        const result = await this.client.invokeCapability("example/greet", {
            name,
        });
        return wrapIfHandle(result, this.client) as string;
    }
}
