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
 * ReferenceExpression represents a reference expression.
 * Supports value mode (format + args) and conditional mode (condition + whenTrue + whenFalse).
 */
class ReferenceExpression {
    // Value mode fields
    private final String format;
    private final Object[] args;

    // Conditional mode fields
    private final Object condition;
    private final ReferenceExpression whenTrue;
    private final ReferenceExpression whenFalse;
    private final String matchValue;
    private final boolean isConditional;

    // Value mode constructor
    ReferenceExpression(String format, Object... args) {
        this.format = format;
        this.args = args;
        this.condition = null;
        this.whenTrue = null;
        this.whenFalse = null;
        this.matchValue = null;
        this.isConditional = false;
    }

    // Conditional mode constructor
    private ReferenceExpression(Object condition, String matchValue, ReferenceExpression whenTrue, ReferenceExpression whenFalse) {
        this.condition = condition;
        this.whenTrue = whenTrue;
        this.whenFalse = whenFalse;
        this.matchValue = matchValue != null ? matchValue : "True";
        this.isConditional = true;
        this.format = null;
        this.args = null;
    }

    String getFormat() {
        return format;
    }

    Object[] getArgs() {
        return args;
    }

    Map<String, Object> toJson() {
        if (isConditional) {
            var condPayload = new java.util.HashMap<String, Object>();
            condPayload.put("condition", AspireClient.serializeValue(condition));
            condPayload.put("whenTrue", whenTrue.toJson());
            condPayload.put("whenFalse", whenFalse.toJson());
            condPayload.put("matchValue", matchValue);

            var result = new java.util.HashMap<String, Object>();
            result.put("$refExpr", condPayload);
            return result;
        }

        Map<String, Object> refExpr = new HashMap<>();
        refExpr.put("format", format);
        refExpr.put("args", Arrays.asList(args));
        
        Map<String, Object> result = new HashMap<>();
        result.put("$refExpr", refExpr);
        return result;
    }

    /**
     * Creates a new reference expression.
     */
    static ReferenceExpression refExpr(String format, Object... args) {
        return new ReferenceExpression(format, args);
    }

    /**
     * Creates a conditional reference expression from its parts.
     */
    static ReferenceExpression createConditional(Object condition, String matchValue, ReferenceExpression whenTrue, ReferenceExpression whenFalse) {
        return new ReferenceExpression(condition, matchValue, whenTrue, whenFalse);
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
            if (result instanceof Handle) {
                resolvedHandle = (Handle) result;
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
            if (result instanceof Handle) {
                resolvedHandle = (Handle) result;
            }
        }
        if (resolvedHandle == null) {
            resolvedHandle = getHandle();
        }
        return resolvedHandle;
    }
}
