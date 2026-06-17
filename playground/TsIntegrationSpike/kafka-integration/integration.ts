import type { DistributedApplicationBuilder, ContainerResource } from '../.modules/aspire.js';
import {
    AspireExport,
    defineIntegration,
    type AspireTypeRef,
} from '../.modules/base.js';

// ============================================================================
// Projection type references used by the AspireExport metadata.
//
// In the long-run these would be derived from the TypeScript signature via
// the codegen / TS compiler API. For this spike the author declares them
// explicitly alongside the function.
// ============================================================================

const builderType: AspireTypeRef = {
    typeId: 'Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder',
    category: 'Handle',
    isInterface: true,
};

const containerType: AspireTypeRef = {
    typeId: 'Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource',
    category: 'Handle',
    isInterface: false,
};

const stringType: AspireTypeRef = {
    typeId: 'string',
    category: 'Primitive',
};

const numberType: AspireTypeRef = {
    typeId: 'number',
    category: 'Primitive',
};

const envMapType: AspireTypeRef = {
    typeId: 'dict',
    category: 'Dict',
    keyType: stringType,
    valueType: stringType,
};

// ============================================================================
// addKafka: the actual integration, hand-written on top of the generated
// Aspire.Hosting typed surface. No engine.* helpers — the impl calls builder
// methods directly like any guest AppHost would.
// ============================================================================

interface AddKafkaArgs
{
    builder: DistributedApplicationBuilder;
    name?: string;
    tag?: string;
    port?: number;
    configure?: (kafka: ContainerResource) => Promise<Record<string, string> | void>;
}

export const addKafka = AspireExport<AddKafkaArgs, ContainerResource>(
    {
        id: 'spike.kafka/addKafka',
        method: 'addKafka',
        description: 'Adds an Apache Kafka broker container',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: builderType.typeId,
            targetType: builderType,
            targetParameterName: 'builder',
            returnsBuilder: true,
            returnType: containerType,
            parameters: [
                { name: 'name', type: stringType },
                { name: 'tag', type: stringType, isOptional: true },
                { name: 'port', type: numberType, isOptional: true },
                {
                    name: 'configure',
                    isOptional: true,
                    isCallback: true,
                    callbackParameters: [
                        { name: 'kafka', type: containerType },
                    ],
                    callbackReturnType: envMapType,
                },
            ],
        },
    },
    async ({ builder, name = 'kafka', tag = '8.1.1', port = 19092, configure }) => {
        console.log(`[@spike/aspire-kafka] addKafka('${name}') starting`);

        let container = await builder
            .addContainer(name, 'confluentinc/confluent-local')
            .withImageTag(tag)
            .withEndpoint({ port, targetPort: 9092, name: 'tcp' })
            .withEndpoint({ targetPort: 9093, name: 'internal' })
            .withEnvironment(
                'KAFKA_LISTENERS',
                'PLAINTEXT://localhost:29092,CONTROLLER://localhost:29093,PLAINTEXT_HOST://0.0.0.0:9092,PLAINTEXT_INTERNAL://0.0.0.0:9093')
            .withEnvironment(
                'KAFKA_LISTENER_SECURITY_PROTOCOL_MAP',
                'CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,PLAINTEXT_HOST:PLAINTEXT,PLAINTEXT_INTERNAL:PLAINTEXT')
            .withEnvironment(
                'KAFKA_ADVERTISED_LISTENERS',
                `PLAINTEXT://localhost:29092,PLAINTEXT_HOST://localhost:${port},PLAINTEXT_INTERNAL://${name}:9093`);

        if (configure) {
            console.log(`[@spike/aspire-kafka] addKafka('${name}') invoking configure callback`);
            const extraEnv = await configure(container);
            console.log(`[@spike/aspire-kafka] addKafka('${name}') configure returned: ${JSON.stringify(extraEnv)}`);

            if (extraEnv) {
                for (const [envName, envValue] of Object.entries(extraEnv)) {
                    container = await container.withEnvironment(envName, envValue);
                }
            }
        }

        console.log(`[@spike/aspire-kafka] addKafka('${name}') complete`);
        return container;
    }
);

export default defineIntegration({
    name: 'KafkaIntegration',
    capabilities: [addKafka],
});
