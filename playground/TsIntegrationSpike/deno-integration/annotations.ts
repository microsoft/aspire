// Typed helpers for ATS annotations.
//
// An annotation is a named, typed bag of state attached to a resource. The transport is a serialized
// JSON string stored under a stable ID (the `withSerializedAnnotation`/`getSerializedAnnotation`/
// `hasSerializedAnnotation` capabilities generated onto every resource), but authors work with a
// declared DTO type. Because the payload is a declared DTO serialized by value, the same annotation
// can be written in one language (for example, C#) and read in another (for example, TypeScript) as
// long as both sides share the ID and the DTO schema.

/**
 * Declares an annotation: a stable identifier paired with the payload type used to (de)serialize it.
 * The `__payload` member is a phantom marker that binds the payload type for inference; it is never
 * present at runtime.
 */
export interface AnnotationDefinition<T> {
    readonly id: string;
    readonly __payload?: T;
}

/**
 * The subset of a resource builder used to read and write serialized annotations. Generated resource
 * wrappers structurally satisfy this interface, so any resource can carry annotations.
 */
export interface SerializedAnnotationStore {
    withSerializedAnnotation(annotationId: string, json: string): PromiseLike<unknown>;
    getSerializedAnnotation(annotationId: string): Promise<string>;
    hasSerializedAnnotation(annotationId: string): Promise<boolean>;
}

/**
 * Declares an annotation with the given stable identifier.
 */
export function defineAnnotation<T>(id: string): AnnotationDefinition<T> {
    return { id };
}

/**
 * Stores a typed annotation, serializing the payload declared by the definition.
 */
export async function setAnnotation<T>(store: SerializedAnnotationStore, definition: AnnotationDefinition<T>, data: T): Promise<void> {
    if (data === undefined) {
        // JSON.stringify(undefined) returns undefined (not a string), which would break the
        // string-typed capability arg, so reject it explicitly with a clear message.
        throw new Error(`Annotation '${definition.id}' value must not be undefined.`);
    }

    await store.withSerializedAnnotation(definition.id, JSON.stringify(data));
}

/**
 * Gets a typed annotation, deserializing the payload declared by the definition. Throws if absent.
 */
export async function getAnnotation<T>(store: SerializedAnnotationStore, definition: AnnotationDefinition<T>): Promise<T> {
    const json = await store.getSerializedAnnotation(definition.id);
    return JSON.parse(json) as T;
}

/**
 * Attempts to get a typed annotation, returning undefined when it has not been set.
 */
export async function tryGetAnnotation<T>(store: SerializedAnnotationStore, definition: AnnotationDefinition<T>): Promise<T | undefined> {
    if (!await store.hasSerializedAnnotation(definition.id)) {
        return undefined;
    }

    return getAnnotation(store, definition);
}
