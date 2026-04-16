import type {
    DistributedApplicationBuilder,
    ContainerResource,
} from '../.modules/aspire.js';

export interface AddKafkaOptions
{
    tag?: string;
    port?: number;
    configure?: (kafka: ContainerResource) => Promise<Record<string, string> | void>;
}

/**
 * Adds an Apache Kafka broker container to the application.
 *
 * Model A integration: runs in the consumer's process, uses the consumer's own
 * AspireClientRpc via the DistributedApplicationBuilder handle it receives.
 * No external capability protocol, no integration host process, no RPC relay —
 * the callback is a plain in-process JS closure.
 */
export async function addKafka(
    builder: DistributedApplicationBuilder,
    name: string,
    options?: AddKafkaOptions): Promise<ContainerResource>
{
    const tag = options?.tag ?? '8.1.1';
    const port = options?.port ?? 19092;

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

    if (options?.configure) {
        const extraEnv = await options.configure(container);
        if (extraEnv) {
            for (const [envName, envValue] of Object.entries(extraEnv)) {
                container = await container.withEnvironment(envName, envValue);
            }
        }
    }

    return container;
}
