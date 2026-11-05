// KubernetesManifestResource.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.HashMap;
import java.util.Map;

/** Wrapper for Aspire.Hosting.Kubernetes/Aspire.Hosting.Kubernetes.KubernetesManifestResource. */
@SuppressWarnings({"all", "unchecked", "serial"})
public class KubernetesManifestResource extends HandleWrapperBase {
    KubernetesManifestResource(Handle handle, AspireClient client) {
        super(handle, client);
    }

    /** Adds or updates a manifest field using a dot-separated path. */
    public KubernetesManifestResource withField(String path, Object value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("path", AspireClient.serializeValue(path));
        reqArgs.put("value", AspireClient.serializeValue(value));
        var result = getClient().invokeCapability("Aspire.Hosting.Kubernetes/withField", reqArgs);
        return (KubernetesManifestResource) result;
    }

}