import type { NativeBuilder, NativeResource } from './generated/aspire.mjs';

type ScalarKind<T> =
    Exclude<T, undefined> extends string ? 'string' :
    Exclude<T, undefined> extends number ? 'number' :
    Exclude<T, undefined> extends boolean ? 'boolean' : never;
export type AnnotationFields<T> = {
    [K in keyof T]-?: { type: ScalarKind<T[K]>; required: boolean };
};
export type AnnotationDefinition<T> = {
    id: string;
    fields: AnnotationFields<T>;
};

export const redisPersistence = defineAnnotation<{
    intervalMs: number;
    keysChangedThreshold: number;
}>('native.redis/persistence', {
    intervalMs: { type: 'number', required: true },
    keysChangedThreshold: { type: 'number', required: true },
});

export function defineAnnotation<T>(id: string, fields: AnnotationFields<T>): AnnotationDefinition<T> {
    return { id, fields };
}

export async function registerAnnotation<T>(builder: NativeBuilder, annotation: AnnotationDefinition<T>): Promise<void> {
    // Object.entries loses the mapped field type; construct a scalar-only
    // registration explicitly rather than sending arbitrary client objects.
    const fields = Object.entries<{ type: 'string' | 'number' | 'boolean'; required: boolean }>(annotation.fields)
        .map(([name, field]) => ({ name, type: field.type, required: field.required }));
    await builder.defineAnnotation(annotation.id, fields);
}

export async function setAnnotation<T>(resource: NativeResource, annotation: AnnotationDefinition<T>, value: NoInfer<T>): Promise<void> {
    const json = JSON.stringify(value);
    if (json === undefined) throw new Error('Annotation payloads must be JSON objects.');
    await resource.withAnnotation(annotation.id, json);
}

export async function getAnnotation<T>(resource: NativeResource, annotation: AnnotationDefinition<T>): Promise<T> {
    // The core validates every write against the registered schema. This helper
    // provides author-language types, not a second payload store or validator.
    return JSON.parse(await resource.getAnnotation(annotation.id));
}
