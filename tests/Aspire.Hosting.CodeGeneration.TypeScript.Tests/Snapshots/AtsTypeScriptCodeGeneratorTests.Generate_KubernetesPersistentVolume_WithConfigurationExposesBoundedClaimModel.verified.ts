withConfiguration(configure: (obj: PersistentVolumeClaim) => Promise<void>): KubernetesPersistentVolumeResourcePromise;

export interface PersistentVolumeClaim {
    toJSON(): MarshalledHandle;
    /**
     * Gets or sets the specification of the PersistentVolumeClaim (PVC) resource.
     *
     * Defines the desired properties and configuration of the PersistentVolumeClaim.
     * This includes storage requirements, access modes, data sources, and selectors.
     * The `PersistentVolumeClaimSpecV1` type provides a complete set of
     * configurable settings for the PVC.
     */
    spec: {
        get: () => PersistentVolumeClaimSpecV1Promise;
        set: (value: Awaitable<PersistentVolumeClaimSpecV1>) => Promise<void>;
    };
    /**
     * Gets or sets the metadata for the Kubernetes resource.
     *
     * The metadata contains standard information such as the resource’s name, namespace, labels, annotations,
     * and other Kubernetes-specific properties. It is encapsulated in an `ObjectMetaV1` object,
     * which provides properties for managing the resource’s unique identifier (UID), name, namespace, generation,
     * and other relevant details like annotations, labels, and owner references.
     */
    metadata: {
        get: () => ObjectMetaV1Promise;
        set: (value: Awaitable<ObjectMetaV1>) => Promise<void>;
    };
}

export interface ObjectMetaV1 {
    toJSON(): MarshalledHandle;
    /**
     * Represents a collection of annotations associated with a Kubernetes object metadata.
     *
     * Annotations are key-value pairs used to store arbitrary non-identifying information about an object.
     * They can be utilized by external tooling or for informational purposes. Unlike labels,
     * annotations are not used to identify or group resources.
     */
    annotations(): Promise<AspireDict<string, string>>;
    /**
     * A collection of key-value pairs used to organize and categorize Kubernetes resources.
     *
     * Labels provide a mechanism to attach metadata to Kubernetes objects, enabling users to select
     * and group resources. These labels can be utilized by controllers for policy application and
     * management operations and can also assist in search and filtering processes.
     */
    readonly labels: AspireDict<string, string>;
}

export interface PersistentVolumeClaimSpecV1 {
    toJSON(): MarshalledHandle;
    /**
     * Gets or sets the name of the storage class required by the PersistentVolumeClaim.
     *
     * The StorageClassName specifies the name of the StorageClass that governs dynamic volume
     * provisioning or volume binding for the associated PersistentVolumeClaim. Setting this property
     * allows the claim to specifically request a volume of a particular storage class. It should match
     * the name of an existing StorageClass in the Kubernetes cluster.
     * If set to `null`, Kubernetes can assign the cluster's default StorageClass.
     * If set to an empty string, the claim requests no StorageClass and can bind only to volumes
     * whose `storageClassName` is also empty. If no default StorageClass is configured and
     * this value is not set, the claim cannot be dynamically provisioned.
     */
    storageClassName: {
        get: () => Promise<string | null>;
        set: (value: string | null) => Promise<void>;
    };
    /**
     * Specifies the volume mode for a PersistentVolumeClaim. This determines how data in the volume is accessed by pods using the claim.
     *
     * Possible values include:
     * - "Filesystem": The volume is mounted as a filesystem.
     * - "Block": The volume is used as a raw block device.
     * This property is important for ensuring compatibility between the volume type
     * and the application accessing it.
     * If `null`, the field is omitted and Kubernetes defaults to filesystem mode.
     */
    volumeMode: {
        get: () => Promise<string | null>;
        set: (value: string | null) => Promise<void>;
    };
    /**
     * Gets or sets the name of the specific volume to be bound to the PersistentVolumeClaim.
     *
     * This property allows the user to bind the PersistentVolumeClaim to a pre-existing PersistentVolume
     * by its name. When specified, the claim is directly associated with the named volume.
     * If `null`, the field is omitted and the claim is not bound to a specific volume by name.
     */
    volumeName: {
        get: () => Promise<string | null>;
        set: (value: string | null) => Promise<void>;
    };
    /**
     * Defines the access modes for a Persistent Volume Claim.
     *
     * Access modes specify the type of access a pod has to the persistent volume. The supported access modes are:
     * - ReadWriteOnce: The volume can be mounted as read-write by a single node.
     * - ReadOnlyMany: The volume can be mounted as read-only by many nodes.
     * - ReadWriteMany: The volume can be mounted as read-write by many nodes.
     */
    accessModes(): Promise<AspireList<string>>;
    /** Gets or sets the resource requirements for the volume. Defines the requested and limit capacities for the volume resources, including storage and other related attributes. */
    resources: {
        get: () => VolumeResourceRequirementsV1Promise;
        set: (value: Awaitable<VolumeResourceRequirementsV1>) => Promise<void>;
    };
}

export interface VolumeResourceRequirementsV1 {
    toJSON(): MarshalledHandle;
    /** Represents the minimum amount of compute resources required for a volume in a Kubernetes environment. Specifies the resource requests for the volume, such as storage capacity or other resource types. Used to define guaranteed resource allocation for the volume. */
    requests(): Promise<AspireDict<string, string>>;
}