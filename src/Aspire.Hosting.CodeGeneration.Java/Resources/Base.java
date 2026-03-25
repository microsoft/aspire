// Base.java - Base types and utilities for Aspire Java SDK
// GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * HandleWrapperBase is the base class for all handle wrappers.
 */
class HandleWrapperBase {
    private final Handle handle;
    private final AspireClient client;

    HandleWrapperBase(Handle handle, AspireClient client) {
        this.handle = handle;
        this.client = client;
    }

    Handle getHandle() {
        return handle;
    }

    AspireClient getClient() {
        return client;
    }
}

/**
 * ResourceBuilderBase extends HandleWrapperBase for resource builders.
 */
class ResourceBuilderBase extends HandleWrapperBase {
    ResourceBuilderBase(Handle handle, AspireClient client) {
        super(handle, client);
    }
}

/**
 * Marker interface for generated enums that need a transport value distinct from Enum.name().
 */
interface WireValueEnum {
    String getValue();
}

/**
 * Represents a runtime union value for generated Java APIs.
 */
final class AspireUnion {
    private final Object value;

    private AspireUnion(Object value) {
        this.value = value;
    }

    static AspireUnion of(Object value) {
        return value instanceof AspireUnion union ? union : new AspireUnion(value);
    }

    static AspireUnion fromValue(Object value) {
        return of(value);
    }

    Object getValue() {
        return value;
    }

    boolean is(Class<?> type) {
        return value != null && type.isInstance(value);
    }

    <T> T getValueAs(Class<T> type) {
        if (value == null) {
            return null;
        }
        if (!type.isInstance(value)) {
            throw new IllegalStateException("Union value is of type " + value.getClass().getName() + ", not " + type.getName());
        }
        return type.cast(value);
    }

    @Override
    public String toString() {
        return "AspireUnion{" + value + "}";
    }
}

@FunctionalInterface
interface AspireAction0 {
    void invoke();
}

@FunctionalInterface
interface AspireAction1<T1> {
    void invoke(T1 arg1);
}

@FunctionalInterface
interface AspireAction2<T1, T2> {
    void invoke(T1 arg1, T2 arg2);
}

@FunctionalInterface
interface AspireAction3<T1, T2, T3> {
    void invoke(T1 arg1, T2 arg2, T3 arg3);
}

@FunctionalInterface
interface AspireAction4<T1, T2, T3, T4> {
    void invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
}

@FunctionalInterface
interface AspireFunc0<R> {
    R invoke();
}

@FunctionalInterface
interface AspireFunc1<T1, R> {
    R invoke(T1 arg1);
}

@FunctionalInterface
interface AspireFunc2<T1, T2, R> {
    R invoke(T1 arg1, T2 arg2);
}

@FunctionalInterface
interface AspireFunc3<T1, T2, T3, R> {
    R invoke(T1 arg1, T2 arg2, T3 arg3);
}

@FunctionalInterface
interface AspireFunc4<T1, T2, T3, T4, R> {
    R invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
}

/**
 * ReferenceExpression represents a reference expression.
 * Supports value mode (format + value providers), conditional mode, and handle mode.
 */
class ReferenceExpression {
    private final String format;
    private final Object[] valueProviders;
    private final Object condition;
    private final ReferenceExpression whenTrue;
    private final ReferenceExpression whenFalse;
    private final String matchValue;
    private final Handle handle;
    private final AspireClient client;

    ReferenceExpression(String format, Object... valueProviders) {
        this.format = format;
        this.valueProviders = valueProviders;
        this.condition = null;
        this.whenTrue = null;
        this.whenFalse = null;
        this.matchValue = null;
        this.handle = null;
        this.client = null;
    }

    private ReferenceExpression(Object condition, String matchValue, ReferenceExpression whenTrue, ReferenceExpression whenFalse) {
        this.format = null;
        this.valueProviders = null;
        this.condition = condition;
        this.whenTrue = whenTrue;
        this.whenFalse = whenFalse;
        this.matchValue = matchValue != null ? matchValue : "True";
        this.handle = null;
        this.client = null;
    }

    ReferenceExpression(Handle handle, AspireClient client) {
        this.format = null;
        this.valueProviders = null;
        this.condition = null;
        this.whenTrue = null;
        this.whenFalse = null;
        this.matchValue = null;
        this.handle = handle;
        this.client = client;
    }

    boolean isConditional() {
        return condition != null;
    }

    boolean isHandle() {
        return handle != null;
    }

    Map<String, Object> toJson() {
        if (handle != null) {
            return handle.toJson();
        }

        Map<String, Object> expression = new HashMap<>();
        if (isConditional()) {
            expression.put("condition", extractValueProvider(condition));
            expression.put("whenTrue", whenTrue.toJson());
            expression.put("whenFalse", whenFalse.toJson());
            expression.put("matchValue", matchValue);
        } else {
            expression.put("format", format);
            if (valueProviders != null && valueProviders.length > 0) {
                List<Object> providers = new ArrayList<>(valueProviders.length);
                for (Object valueProvider : valueProviders) {
                    providers.add(extractValueProvider(valueProvider));
                }
                expression.put("valueProviders", providers);
            }
        }

        Map<String, Object> result = new HashMap<>();
        result.put("$expr", expression);
        return result;
    }

    public String getValue() {
        return getValue(null);
    }

    public String getValue(CancellationToken cancellationToken) {
        if (handle == null || client == null) {
            throw new IllegalStateException("getValue is only available on server-returned ReferenceExpression instances");
        }

        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(handle));
        if (cancellationToken != null) {
            reqArgs.put("cancellationToken", client.registerCancellation(cancellationToken));
        }

        return (String) client.invokeCapability("Aspire.Hosting.ApplicationModel/getValue", reqArgs);
    }

    public static ReferenceExpression refExpr(String format, Object... valueProviders) {
        return new ReferenceExpression(format, valueProviders);
    }

    public static ReferenceExpression createConditional(Object condition, String matchValue, ReferenceExpression whenTrue, ReferenceExpression whenFalse) {
        return new ReferenceExpression(condition, matchValue, whenTrue, whenFalse);
    }

    private static Object extractValueProvider(Object value) {
        if (value == null) {
            throw new IllegalArgumentException("Cannot use null in a reference expression");
        }

        if (value instanceof String || value instanceof Number || value instanceof Boolean) {
            return value;
        }

        return AspireClient.serializeValue(value);
    }
}

/**
 * AspireList is a handle-backed list with lazy handle resolution.
 */
class AspireList<T> extends HandleWrapperBase {
    private final String getterCapabilityId;
    private Handle resolvedHandle;

    AspireList(Handle handle, AspireClient client) {
        super(handle, client);
        this.getterCapabilityId = null;
        this.resolvedHandle = handle;
    }

    AspireList(Handle contextHandle, AspireClient client, String getterCapabilityId) {
        super(contextHandle, client);
        this.getterCapabilityId = getterCapabilityId;
        this.resolvedHandle = null;
    }

    private Handle ensureHandle() {
        if (resolvedHandle != null) {
            return resolvedHandle;
        }
        if (getterCapabilityId != null) {
            Map<String, Object> args = new HashMap<>();
            args.put("context", getHandle().toJson());
            Object result = getClient().invokeCapability(getterCapabilityId, args);
            if (result instanceof Handle handle) {
                resolvedHandle = handle;
            }
        }
        if (resolvedHandle == null) {
            resolvedHandle = getHandle();
        }
        return resolvedHandle;
    }
}

/**
 * AspireDict is a handle-backed dictionary with lazy handle resolution.
 */
class AspireDict<K, V> extends HandleWrapperBase {
    private final String getterCapabilityId;
    private Handle resolvedHandle;

    AspireDict(Handle handle, AspireClient client) {
        super(handle, client);
        this.getterCapabilityId = null;
        this.resolvedHandle = handle;
    }

    AspireDict(Handle contextHandle, AspireClient client, String getterCapabilityId) {
        super(contextHandle, client);
        this.getterCapabilityId = getterCapabilityId;
        this.resolvedHandle = null;
    }

    private Handle ensureHandle() {
        if (resolvedHandle != null) {
            return resolvedHandle;
        }
        if (getterCapabilityId != null) {
            Map<String, Object> args = new HashMap<>();
            args.put("context", getHandle().toJson());
            Object result = getClient().invokeCapability(getterCapabilityId, args);
            if (result instanceof Handle handle) {
                resolvedHandle = handle;
            }
        }
        if (resolvedHandle == null) {
            resolvedHandle = getHandle();
        }
        return resolvedHandle;
    }
}

/**
 * Registers runtime-owned wrappers defined in Base.java.
 */
final class BaseRegistrations {
    private BaseRegistrations() {
    }

    static {
        AspireClient.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", ReferenceExpression::new);
    }

    static void ensureRegistered() {
    }
}
