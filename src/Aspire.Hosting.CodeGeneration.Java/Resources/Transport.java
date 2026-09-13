// Transport.java - JSON-RPC transport layer for the Aspire Java SDK.
//
// This is a hand-maintained template, not generated output: it is embedded into
// Aspire.Hosting.CodeGeneration.Java and split into one file per type alongside the types the
// generator emits. Everything above the package declaration is dropped during that split, and each
// emitted file gets its own "GENERATED CODE - DO NOT EDIT" banner from CreateJavaSourceFile, so
// edit this file to change what users receive.

package aspire;

import java.io.FileDescriptor;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.HashSet;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BiFunction;
import java.util.function.Consumer;
import java.util.function.Function;

/**
 * Handle represents a remote object reference.
 */
class Handle {
    private final String id;
    private final String typeId;

    Handle(String id, String typeId) {
        this.id = id;
        this.typeId = typeId;
    }

    String getId() { return id; }
    String getTypeId() { return typeId; }

    Map<String, Object> toJson() {
        Map<String, Object> result = new HashMap<>();
        result.put("$handle", id);
        result.put("$type", typeId);
        return result;
    }

    @Override
    public String toString() {
        return "Handle{id='" + id + "', typeId='" + typeId + "'}";
    }
}

/**
 * CapabilityError represents an error from a capability invocation.
 */
class CapabilityError extends RuntimeException {
    private final String code;
    private final Object data;

    CapabilityError(String code, String message, Object data) {
        super(message);
        this.code = code;
        this.data = data;
    }

    public String getCode() { return code; }
    public Object getData() { return data; }
}

/**
 * CancellationToken for cancelling operations.
 */
public class CancellationToken {
    private final AtomicBoolean cancelled = new AtomicBoolean(false);
    private final AtomicInteger remoteReferences = new AtomicInteger(0);
    private final Object cancellationLock = new Object();
    private final List<Runnable> listeners = new ArrayList<>();

    // Remote token id supplied by the AppHost when this token is materialized for a
    // callback argument. Null for locally-created tokens. Retained so cancellation can
    // be correlated back to the AppHost if needed.
    private final String remoteTokenId;
    private final AspireClient remoteClient;

    public CancellationToken() {
        this.remoteTokenId = null;
        this.remoteClient = null;
    }

    CancellationToken(String remoteTokenId, AspireClient remoteClient) {
        this.remoteTokenId = remoteTokenId;
        this.remoteClient = remoteClient;
    }

    /**
     * Materializes a cancellation token from a transport value sent by the AppHost.
     * When the AppHost invokes a callback that accepts a CancellationToken it passes a
     * remote token id (a string); generated code calls this to turn that wire value into
     * a CancellationToken instance. Mirrors the TypeScript/Go SDK behavior.
     */
    public static CancellationToken fromValue(Object value) {
        if (value instanceof CancellationToken token) {
            return token;
        }
        if (value instanceof String tokenId) {
            AspireClient client = AspireClient.currentCallbackClient();
            return client == null
                ? new CancellationToken(tokenId, null)
                : client.getOrCreateRemoteCancellationToken(tokenId);
        }
        return new CancellationToken();
    }

    String getRemoteTokenId() { return remoteTokenId; }

    void retainRemoteReference() {
        remoteReferences.incrementAndGet();
    }

    void releaseRemoteReference() {
        if (remoteTokenId != null && remoteClient != null) {
            remoteClient.releaseRemoteCancellationToken(remoteTokenId, this);
        }
    }

    int decrementRemoteReference() {
        return remoteReferences.decrementAndGet();
    }

    public void cancel() {
        if (!markCancelled()) {
            return;
        }

        if (remoteTokenId != null && remoteClient != null) {
            remoteClient.removeRemoteCancellationToken(remoteTokenId, this);
        }

        notifyCancellationListeners();
    }

    boolean markCancelled() {
        synchronized (cancellationLock) {
            return cancelled.compareAndSet(false, true);
        }
    }

    void notifyCancellationListeners() {
        List<Runnable> listenersToNotify;
        synchronized (cancellationLock) {
            listenersToNotify = new ArrayList<>(listeners);
            listeners.clear();
        }
        RuntimeException listenerFailure = null;
        for (Runnable listener : listenersToNotify) {
            try {
                listener.run();
            } catch (RuntimeException exception) {
                if (listenerFailure == null) {
                    listenerFailure = exception;
                } else if (listenerFailure != exception) {
                    listenerFailure.addSuppressed(exception);
                }
            }
        }
        if (listenerFailure != null) {
            throw listenerFailure;
        }
    }

    public boolean isCancelled() { return cancelled.get(); }

    public void onCancel(Runnable listener) {
        synchronized (cancellationLock) {
            if (!cancelled.get()) {
                listeners.add(listener);
                return;
            }
        }
        listener.run();
    }

    void removeCancelListener(Runnable listener) {
        synchronized (cancellationLock) {
            listeners.remove(listener);
        }
    }

}

interface JsonSerializable {
    Map<String, Object> toMap();
}

/**
 * AspireClient handles JSON-RPC communication with the AppHost server.
 */
class AspireClient {
    private static final boolean DEBUG = System.getenv("ASPIRE_DEBUG") != null;
    private static final int MAX_PENDING_REMOTE_CANCELLATIONS = 1024;
    private static final ThreadLocal<AspireClient> callbackClient = new ThreadLocal<>();
    private static final ThreadLocal<List<CancellationToken>> callbackRemoteTokens = new ThreadLocal<>();
    
    private final String socketPath;
    private OutputStream outputStream;
    private InputStream inputStream;
    // Windows only. inputStream/outputStream are built from this file's descriptor, so the
    // RandomAccessFile has to outlive connect() -- closing it would close the descriptor they share
    // and break the connection. Holding it in a field also makes that lifetime explicit to a
    // compiler's resource-leak analysis, which cannot see the escape through FileDescriptor.
    private RandomAccessFile namedPipe;
    private final AtomicInteger requestId = new AtomicInteger(0);
    private final Map<String, Function<Object[], Object>> callbacks = new ConcurrentHashMap<>();
    private final Map<String, Consumer<Void>> cancellations = new ConcurrentHashMap<>();
    private final Map<Integer, CompletableFuture<Object>> pendingRequests = new ConcurrentHashMap<>();
    private final Map<String, CancellationRegistration> cancellationRegistrations = new ConcurrentHashMap<>();
    private final Map<Object, String> activeCallbackRequests = new ConcurrentHashMap<>();
    private final Map<String, CancellationToken> remoteCancellationTokens = new ConcurrentHashMap<>();
    private final Map<String, CancellationToken> pendingRemoteCancellations = new LinkedHashMap<>();
    private final Object readerLock = new Object();
    private final Object connectionStateLock = new Object();
    private volatile boolean readerStarted;
    private volatile boolean disconnected;
    private volatile Throwable disconnectCause;
    private Runnable disconnectHandler;
    private int activeServerCallbacks;

    // Handle wrapper factory registry
    private static final Map<String, BiFunction<Handle, AspireClient, Object>> handleWrappers = new ConcurrentHashMap<>();

    public static void registerHandleWrapper(String typeId, BiFunction<Handle, AspireClient, Object> factory) {
        handleWrappers.put(typeId, factory);
    }

    public AspireClient(String socketPath) {
        this.socketPath = socketPath;
    }

    static AspireClient currentCallbackClient() {
        return callbackClient.get();
    }

    CancellationToken getOrCreateRemoteCancellationToken(String cancellationId) {
        CancellationToken token;
        synchronized (connectionStateLock) {
            if (disconnected) {
                token = new CancellationToken(cancellationId, this);
                token.retainRemoteReference();
                token.markCancelled();
            } else {
                CancellationToken pendingCancellation = pendingRemoteCancellations.remove(cancellationId);
                token = remoteCancellationTokens.compute(cancellationId, (id, existing) -> {
                    CancellationToken retained = existing != null
                        ? existing
                        : pendingCancellation != null ? pendingCancellation : new CancellationToken(id, this);
                    retained.retainRemoteReference();
                    return retained;
                });
            }
        }
        callbackRemoteTokens.get().add(token);
        return token;
    }

    void removeRemoteCancellationToken(String cancellationId, CancellationToken token) {
        remoteCancellationTokens.remove(cancellationId, token);
    }

    void releaseRemoteCancellationToken(String cancellationId, CancellationToken token) {
        remoteCancellationTokens.computeIfPresent(cancellationId, (id, existing) -> {
            if (existing != token) {
                return existing;
            }
            return token.decrementRemoteReference() == 0 ? null : token;
        });
    }

    private boolean cancelRemoteCancellationToken(String cancellationId) {
        CancellationToken token;
        boolean notifyListeners;
        synchronized (connectionStateLock) {
            if (disconnected) {
                return false;
            }
            token = remoteCancellationTokens.get(cancellationId);
            if (token == null && activeServerCallbacks == 0) {
                return false;
            }
            if (token == null) {
                token = pendingRemoteCancellations.computeIfAbsent(
                    cancellationId,
                    id -> new CancellationToken(id, this));
            } else if (activeServerCallbacks > 0) {
                pendingRemoteCancellations.put(cancellationId, token);
            }
            while (pendingRemoteCancellations.size() > MAX_PENDING_REMOTE_CANCELLATIONS) {
                String oldestId = pendingRemoteCancellations.keySet().iterator().next();
                pendingRemoteCancellations.remove(oldestId);
            }
            notifyListeners = token.markCancelled();
        }

        if (notifyListeners) {
            token.notifyCancellationListeners();
        }
        return true;
    }

    public void connect() throws IOException {
        debug("Connecting to AppHost server at " + socketPath);
        
        if (isWindows()) {
            connectWindowsNamedPipe();
        } else {
            connectUnixSocket();
        }

        ensureReaderLoopStarted();
        
        debug("Connected successfully");
    }

    private boolean isWindows() {
        return System.getProperty("os.name").toLowerCase().contains("win");
    }

    private void connectWindowsNamedPipe() throws IOException {
        // Extract just the filename from the socket path for the named pipe
        String pipeName = new java.io.File(socketPath).getName();
        String pipePath = "\\\\.\\pipe\\" + pipeName;
        debug("Opening Windows named pipe: " + pipePath);
        
        // Use RandomAccessFile to open the named pipe
        namedPipe = new RandomAccessFile(pipePath, "rw");

        // Create streams from the RandomAccessFile
        FileDescriptor fd = namedPipe.getFD();
        inputStream = new FileInputStream(fd);
        outputStream = new FileOutputStream(fd);
        
        debug("Named pipe opened successfully");
    }

    private void connectUnixSocket() throws IOException {
        // Use Java 16+ Unix domain socket support
        debug("Opening Unix domain socket: " + socketPath);
        var address = java.net.UnixDomainSocketAddress.of(socketPath);
        var channel = java.nio.channels.SocketChannel.open(address);
        
        // Create streams from the channel
        inputStream = java.nio.channels.Channels.newInputStream(channel);
        outputStream = java.nio.channels.Channels.newOutputStream(channel);
        
        debug("Unix domain socket opened successfully");
    }

    public void onDisconnect(Runnable handler) {
        boolean invokeImmediately;
        synchronized (connectionStateLock) {
            invokeImmediately = disconnected;
            if (!invokeImmediately) {
                disconnectHandler = handler;
            }
        }

        if (invokeImmediately) {
            handler.run();
        }
    }

    public Object invokeCapability(String capabilityId, Map<String, Object> args) {
        List<String> cancellationIds = new ArrayList<>();

        try {
            Map<String, Object> params = new HashMap<>();
            params.put("capabilityId", capabilityId);
            params.put("args", marshalTransportValue(args, cancellationIds));
            var uniqueCancellationIds = new HashSet<>(cancellationIds);

            return sendRequest("invokeCapability", params, () -> {
                for (String cancellationId : uniqueCancellationIds) {
                    CancellationRegistration registration = cancellationRegistrations.get(cancellationId);
                    if (registration != null) {
                        registration.enable();
                    }
                }
            });
        } finally {
            for (String cancellationId : new HashSet<>(cancellationIds)) {
                unregisterCancellation(cancellationId);
            }
        }
    }

    private Object sendRequest(String method, Object params) {
        return sendRequest(method, params, null);
    }

    private Object sendRequest(String method, Object params, Runnable requestSent) {
        CompletableFuture<Object> pendingResponse = new CompletableFuture<>();
        int id;
        synchronized (connectionStateLock) {
            if (disconnected) {
                throw disconnectedException();
            }

            id = requestId.incrementAndGet();
            pendingRequests.put(id, pendingResponse);
        }

        Map<String, Object> request = new HashMap<>();
        request.put("jsonrpc", "2.0");
        request.put("id", id);
        request.put("method", method);
        request.put("params", params);

        debug("Sending request " + method + " with id=" + id);

        try {
            ensureReaderLoopStarted();
            sendMessage(request);
            if (requestSent != null) {
                requestSent.run();
            }
        } catch (IOException e) {
            pendingRequests.remove(id);
            handleDisconnect();
            throw new RuntimeException("Failed to send request " + method + ": " + e.getMessage(), e);
        }

        try {
            Object result = pendingResponse.join();
            return unwrapResult(result);
        } catch (CompletionException completionException) {
            Throwable cause = completionException.getCause();
            if (cause instanceof RuntimeException runtimeException) {
                throw runtimeException;
            }
            throw new RuntimeException("Request " + method + " failed", cause);
        }
    }

    @SuppressWarnings("unchecked")
    private Object marshalTransportValue(Object value) {
        return marshalTransportValue(value, null);
    }

    @SuppressWarnings("unchecked")
    private Object marshalTransportValue(Object value, List<String> cancellationIds) {
        if (value == null) {
            return null;
        }

        if (value instanceof CancellationToken token) {
            String cancellationId = registerCancellation(token, false);
            if (cancellationId != null && cancellationIds != null && cancellationRegistrations.containsKey(cancellationId)) {
                cancellationIds.add(cancellationId);
            }
            return cancellationId;
        }

        if (value instanceof Function<?, ?> function) {
            Function<Object, Object> typedFunction = (Function<Object, Object>) function;
            return registerCallback(args -> typedFunction.apply(args.length > 0 ? args[0] : null));
        }

        Object serialized = serializeValue(value);
        if (serialized instanceof Map) {
            Map<String, Object> map = (Map<String, Object>) serialized;
            Map<String, Object> result = new HashMap<>();
            for (Map.Entry<String, Object> entry : map.entrySet()) {
                result.put(entry.getKey(), marshalTransportValue(entry.getValue(), cancellationIds));
            }
            return result;
        }
        if (serialized instanceof List) {
            List<Object> list = (List<Object>) serialized;
            List<Object> result = new ArrayList<>();
            for (Object item : list) {
                result.add(marshalTransportValue(item, cancellationIds));
            }
            return result;
        }
        if (serialized instanceof Object[] array) {
            List<Object> result = new ArrayList<>();
            for (Object item : array) {
                result.add(marshalTransportValue(item, cancellationIds));
            }
            return result;
        }

        return serialized;
    }

    public void authenticate(String token) {
        Object result = sendRequest("authenticate", List.of(token));
        if (!(result instanceof Boolean authenticated) || !authenticated) {
            throw new RuntimeException("Failed to authenticate to the AppHost server.");
        }
    }

    private void sendMessage(Map<String, Object> message) throws IOException {
        String json = toJson(message);
        byte[] content = json.getBytes(StandardCharsets.UTF_8);
        String header = "Content-Length: " + content.length + "\r\n\r\n";
        
        debug("Writing message: " + message.get("method") + " (id=" + message.get("id") + ")");
        
        synchronized (outputStream) {
            outputStream.write(header.getBytes(StandardCharsets.UTF_8));
            outputStream.write(content);
            outputStream.flush();
        }
    }

    private void ensureReaderLoopStarted() {
        synchronized (readerLock) {
            if (readerStarted) {
                return;
            }

            if (inputStream == null) {
                throw new IllegalStateException("Input stream is not initialized");
            }

            readerStarted = true;

            Thread readerThread = new Thread(this::readLoop, "aspire-client-reader");
            readerThread.setDaemon(true);
            readerThread.start();
        }
    }

    private void readLoop() {
        try {
            while (true) {
                Map<String, Object> message = readMessage();
                routeMessage(message);
            }
        } catch (Exception e) {
            disconnectCause = e;
            handleDisconnect();
        }
    }

    @SuppressWarnings("unchecked")
    private void routeMessage(Map<String, Object> message) throws IOException {
        if (message.containsKey("method")) {
            if ("$/cancelRequest".equals(message.get("method"))) {
                routeCancellationNotification(message.get("params"));
                return;
            }

            boolean isCallback = "invokeCallback".equals(message.get("method"));
            Object callbackRequestId = isCallback ? message.get("id") : null;
            String callbackCancellationId = isCallback ? getCallbackCancellationId(message.get("params")) : null;
            if (isCallback) {
                synchronized (connectionStateLock) {
                    activeServerCallbacks++;
                }
                if (callbackRequestId != null && callbackCancellationId != null) {
                    activeCallbackRequests.put(callbackRequestId, callbackCancellationId);
                }
            }
            try {
                Thread.startVirtualThread(() -> {
                    try {
                        handleServerRequest(message);
                    } catch (IOException e) {
                        disconnectCause = e;
                        handleDisconnect();
                    } finally {
                        if (isCallback) {
                            completeServerCallback(callbackRequestId, callbackCancellationId);
                        }
                    }
                });
            } catch (RuntimeException e) {
                if (isCallback) {
                    completeServerCallback(callbackRequestId, callbackCancellationId);
                }
                throw e;
            }
            return;
        }

        Integer responseId = toNumericId(message.get("id"));
        if (responseId == null) {
            throw new IOException("Invalid JSON-RPC response: numeric id is required.");
        }

        CompletableFuture<Object> pendingResponse = pendingRequests.get(responseId);
        if (pendingResponse == null) {
            return;
        }

        if (message.containsKey("error")) {
            Map<String, Object> error = (Map<String, Object>) message.get("error");
            String code = String.valueOf(error.get("code"));
            String errorMessage = String.valueOf(error.get("message"));
            Object data = error.get("data");
            pendingResponse.completeExceptionally(new CapabilityError(code, errorMessage, data));
            pendingRequests.remove(responseId, pendingResponse);
            return;
        }

        pendingResponse.complete(message.get("result"));
        pendingRequests.remove(responseId, pendingResponse);
    }

    private void completeServerCallback(Object callbackRequestId, String callbackCancellationId) {
        if (callbackRequestId != null && callbackCancellationId != null) {
            activeCallbackRequests.remove(callbackRequestId, callbackCancellationId);
        }
        synchronized (connectionStateLock) {
            activeServerCallbacks--;
            if (activeServerCallbacks == 0) {
                pendingRemoteCancellations.clear();
            }
        }
    }

    private Integer toNumericId(Object id) {
        if (id instanceof Number number) {
            try {
                return new BigDecimal(number.toString()).intValueExact();
            } catch (ArithmeticException | NumberFormatException ignored) {
                return null;
            }
        }

        return null;
    }

    @SuppressWarnings("unchecked")
    private Map<String, Object> readMessage() throws IOException {
        // Read headers
        int contentLength = -1;
        
        while (true) {
            String line = readLine();
            if (line.isEmpty()) {
                break;
            }
            if (line.startsWith("Content-Length:")) {
                contentLength = Integer.parseInt(line.substring(15).trim());
            }
        }
        
        if (contentLength < 0) {
            throw new IOException("No Content-Length header found");
        }
        
        // Read body
        byte[] body = new byte[contentLength];
        int totalRead = 0;
        while (totalRead < contentLength) {
            int read = inputStream.read(body, totalRead, contentLength - totalRead);
            if (read < 0) {
                throw new IOException("Unexpected end of stream");
            }
            totalRead += read;
        }
        
        String json = new String(body, StandardCharsets.UTF_8);
        debug("Received: " + json.substring(0, Math.min(200, json.length())) + "...");
        
        return (Map<String, Object>) parseJson(json);
    }

    private String readLine() throws IOException {
        StringBuilder sb = new StringBuilder();
        int ch;
        while ((ch = inputStream.read()) != -1) {
            if (ch == '\r') {
                int next = inputStream.read();
                if (next == '\n') {
                    break;
                }
                sb.append((char) ch);
                if (next != -1) sb.append((char) next);
            } else if (ch == '\n') {
                break;
            } else {
                sb.append((char) ch);
            }
        }
        return sb.toString();
    }

    private void routeCancellationNotification(Object params) {
        Object callbackRequestId = getCancelledRequestId(params);
        if (callbackRequestId == null) {
            return;
        }

        // Resolve the callback request id on the reader before callback completion removes
        // its correlation entry. Only listener execution moves to the virtual thread.
        String cancellationId = activeCallbackRequests.get(callbackRequestId);
        if (cancellationId == null) {
            return;
        }

        Thread.startVirtualThread(() -> {
            try {
                cancelRemoteCancellationToken(cancellationId);
            } catch (RuntimeException exception) {
                debug("Cancellation listener failed.", exception);
            }
        });
    }

    @SuppressWarnings("unchecked")
    private void handleServerRequest(Map<String, Object> request) throws IOException {
        String method = (String) request.get("method");
        Object idObj = request.get("id");
        Object params = request.get("params");

        debug("Received server request: " + method);

        Object result = null;
        Map<String, Object> error = null;

        try {
            if ("invokeCallback".equals(method)) {
                String callbackId = getCallbackId(params);
                if (callbackId == null) {
                    error = createError(-32602, "Invalid params: callbackId is required.");
                } else {
                    List<Object> args = getCallbackArgs(params);

                    Function<Object[], Object> callback = callbacks.get(callbackId);
                    if (callback != null) {
                        Object[] unwrappedArgs = args.stream()
                            .map(this::unwrapResult)
                            .toArray();
                        AspireClient previousCallbackClient = callbackClient.get();
                        List<CancellationToken> previousRemoteTokens = callbackRemoteTokens.get();
                        List<CancellationToken> acquiredRemoteTokens = new ArrayList<>();
                        callbackClient.set(this);
                        callbackRemoteTokens.set(acquiredRemoteTokens);
                        try {
                            result = awaitValue(callback.apply(unwrappedArgs));
                        } finally {
                            for (CancellationToken token : acquiredRemoteTokens) {
                                token.releaseRemoteReference();
                            }
                            if (previousCallbackClient == null) {
                                callbackClient.remove();
                            } else {
                                callbackClient.set(previousCallbackClient);
                            }
                            if (previousRemoteTokens == null) {
                                callbackRemoteTokens.remove();
                            } else {
                                callbackRemoteTokens.set(previousRemoteTokens);
                            }
                        }
                    } else {
                        error = createError(-32601, "Callback not found: " + callbackId);
                    }
                }
            } else if ("cancel".equals(method) || "cancelToken".equals(method)) {
                String cancellationId = getCancellationId(params);
                if (cancellationId == null) {
                    error = createError(-32602, "Invalid params: cancellationId is required.");
                } else {
                    boolean cancelled = cancelRemoteCancellationToken(cancellationId);
                    Consumer<Void> handler = cancellations.get(cancellationId);
                    if (handler != null) {
                        handler.accept(null);
                        cancelled = true;
                    }
                    result = cancelled;
                }
            } else {
                error = createError(-32601, "Unknown method: " + method);
            }
        } catch (Exception e) {
            error = createError(-32603, e.getMessage());
        }

        if (!request.containsKey("id")) {
            return;
        }

        // Send response
        Map<String, Object> response = new HashMap<>();
        response.put("jsonrpc", "2.0");
        response.put("id", idObj);
        if (error != null) {
            response.put("error", error);
        } else {
            response.put("result", serializeValue(result));
        }
        
        sendMessage(response);
    }

    @SuppressWarnings("unchecked")
    private String getCallbackId(Object params) {
        if (params instanceof List<?> list && !list.isEmpty()) {
            return asString(list.get(0));
        }

        if (params instanceof Map<?, ?> map) {
            return asString(map.get("callbackId"));
        }

        return null;
    }

    private String getCallbackCancellationId(Object params) {
        Object args = null;
        if (params instanceof List<?> list && list.size() > 1) {
            args = list.get(1);
        } else if (params instanceof Map<?, ?> map) {
            args = map.get("args");
        }

        // Generated cancellable callbacks include the token id in both its positional slot and:
        //   "args": { "p0": "<token-id>", "$cancellationToken": "<token-id>" }
        // The named entry lets transport cancellation find the token without knowing its position.
        if (args instanceof Map<?, ?> map) {
            return asString(map.get("$cancellationToken"));
        }

        return null;
    }

    private Object getCancelledRequestId(Object params) {
        // StreamJsonRpc cancels an invocation with:
        //   { "jsonrpc": "2.0", "method": "$/cancelRequest", "params": { "id": <request-id> } }
        // Preserve the parsed id type so a string id such as "41" never matches numeric id 41.
        if (params instanceof Map<?, ?> map) {
            Object id = map.get("id");
            if (id instanceof String || id instanceof Number) {
                return id;
            }
        }

        return null;
    }

    @SuppressWarnings("unchecked")
    private List<Object> getCallbackArgs(Object params) {
        Object args = null;
        if (params instanceof List<?> list && list.size() > 1) {
            args = list.get(1);
        } else if (params instanceof Map<?, ?> map) {
            args = map.get("args");
        }

        if (args instanceof Map<?, ?> map) {
            List<Object> positionalArgs = new ArrayList<>();
            for (var i = 0; ; i++) {
                var key = "p" + i;
                if (map.containsKey(key)) {
                    positionalArgs.add(map.get(key));
                } else {
                    break;
                }
            }
            return positionalArgs;
        }

        if (args instanceof List<?> list) {
            return (List<Object>) list;
        }

        return args == null ? List.of() : List.of(args);
    }

    private String getCancellationId(Object params) {
        if (params instanceof String id) {
            return id;
        }

        if (params instanceof List<?> list && !list.isEmpty()) {
            return asString(list.get(0));
        }

        if (params instanceof Map<?, ?> map) {
            return asString(map.get("cancellationId"));
        }

        return null;
    }

    private String asString(Object value) {
        return value instanceof String string ? string : null;
    }

    private Map<String, Object> createError(int code, String message) {
        Map<String, Object> error = new HashMap<>();
        error.put("code", code);
        error.put("message", message);
        return error;
    }

    @SuppressWarnings("unchecked")
    private Object unwrapResult(Object value) {
        if (value == null) {
            return null;
        }
        
        if (value instanceof Map) {
            Map<String, Object> map = (Map<String, Object>) value;
            
            // Check for handle
            if (map.containsKey("$handle")) {
                String handleId = (String) map.get("$handle");
                String typeId = (String) map.get("$type");
                Handle handle = new Handle(handleId, typeId);
                
                BiFunction<Handle, AspireClient, Object> factory = handleWrappers.get(typeId);
                if (factory != null) {
                    return factory.apply(handle, this);
                }
                return handle;
            }
            
            // Check for error
            if (map.containsKey("$error")) {
                Map<String, Object> errorData = (Map<String, Object>) map.get("$error");
                String code = String.valueOf(errorData.get("code"));
                String message = String.valueOf(errorData.get("message"));
                throw new CapabilityError(code, message, errorData.get("data"));
            }
            
            // Recursively unwrap map values
            Map<String, Object> result = new HashMap<>();
            for (Map.Entry<String, Object> entry : map.entrySet()) {
                result.put(entry.getKey(), unwrapResult(entry.getValue()));
            }
            return result;
        }
        
        if (value instanceof List) {
            List<Object> list = (List<Object>) value;
            List<Object> result = new ArrayList<>();
            for (Object item : list) {
                result.add(unwrapResult(item));
            }
            return result;
        }
        
        return value;
    }

    private void handleDisconnect() {
        Runnable handler;
        List<CancellationToken> tokensToNotify = new ArrayList<>();
        synchronized (connectionStateLock) {
            if (disconnected) {
                return;
            }

            disconnected = true;
            failAllPendingRequests(disconnectedException());
            for (CancellationToken remoteToken : remoteCancellationTokens.values()) {
                if (remoteToken.markCancelled()) {
                    tokensToNotify.add(remoteToken);
                }
            }
            for (CancellationToken pendingToken : pendingRemoteCancellations.values()) {
                if (pendingToken.markCancelled()) {
                    tokensToNotify.add(pendingToken);
                }
            }
            remoteCancellationTokens.clear();
            pendingRemoteCancellations.clear();
            activeCallbackRequests.clear();
            handler = disconnectHandler;
        }

        for (CancellationToken remoteToken : tokensToNotify) {
            try {
                remoteToken.notifyCancellationListeners();
            } catch (RuntimeException exception) {
                debug("Cancellation listener failed during disconnect.", exception);
            }
        }

        if (handler != null) {
            handler.run();
        }
    }

    private RuntimeException disconnectedException() {
        Throwable cause = disconnectCause;
        return cause == null
            ? new RuntimeException("Disconnected from AppHost")
            : new RuntimeException("Disconnected from AppHost", cause);
    }

    private void failAllPendingRequests(RuntimeException exception) {
        for (Map.Entry<Integer, CompletableFuture<Object>> entry : pendingRequests.entrySet()) {
            entry.getValue().completeExceptionally(exception);
        }
        pendingRequests.clear();
    }

    public String registerCallback(Function<Object[], Object> callback) {
        String id = UUID.randomUUID().toString();
        callbacks.put(id, callback);
        return id;
    }

    public String registerCancellation(CancellationToken token) {
        return registerCancellation(token, true);
    }

    private String registerCancellation(CancellationToken token, boolean enabled) {
        if (token == null) {
            return null;
        }

        String remoteTokenId = token.getRemoteTokenId();
        if (remoteTokenId != null) {
            return remoteTokenId;
        }

        String id = UUID.randomUUID().toString();
        var registration = new CancellationRegistration(id, token, enabled);
        cancellationRegistrations.put(id, registration);
        registration.attach();
        return id;
    }

    public void unregisterCancellation(String cancellationId) {
        CancellationRegistration registration = cancellationRegistrations.remove(cancellationId);
        if (registration != null) {
            registration.dispose();
        }
    }

    private final class CancellationRegistration {
        private final String id;
        private final CancellationToken token;
        private final Runnable listener;
        private final AtomicBoolean enabled;
        private final AtomicBoolean requested = new AtomicBoolean(false);
        private final AtomicBoolean sent = new AtomicBoolean(false);

        CancellationRegistration(String id, CancellationToken token, boolean enabled) {
            this.id = id;
            this.token = token;
            this.enabled = new AtomicBoolean(enabled);
            this.listener = this::requestCancellation;
        }

        void attach() {
            token.onCancel(listener);
        }

        void enable() {
            enabled.set(true);
            trySend();
        }

        void dispose() {
            token.removeCancelListener(listener);
        }

        private void requestCancellation() {
            requested.set(true);
            trySend();
        }

        private void trySend() {
            if (enabled.get() && requested.get() && sent.compareAndSet(false, true)) {
                sendCancellationRequest(id);
            }
        }
    }

    private void sendCancellationRequest(String cancellationId) {
        CompletableFuture.runAsync(() -> {
            try {
                sendRequest("cancelToken", List.of(cancellationId));
            } catch (RuntimeException ignored) {
                // Cancellation is best-effort. The host may already have completed the operation.
            }
        });
    }

    public static Object awaitValue(Object value) {
        if (value instanceof CompletionStage<?> stage) {
            return stage.toCompletableFuture().join();
        }
        return value;
    }

    public static Object convertArray(Object value, Class<?> componentType, Function<Object, Object> converter) {
        List<?> values = (List<?>) value;
        Object array = java.lang.reflect.Array.newInstance(componentType, values.size());
        for (int i = 0; i < values.size(); i++) {
            java.lang.reflect.Array.set(array, i, converter.apply(values.get(i)));
        }
        return array;
    }

    // Simple JSON serialization (no external dependencies)
    public static Object serializeValue(Object value) {
        if (value == null) {
            return null;
        }
        if (value instanceof Handle) {
            return ((Handle) value).toJson();
        }
        if (value instanceof HandleWrapperBase) {
            return ((HandleWrapperBase) value).getHandle().toJson();
        }
        if (value instanceof ReferenceExpression) {
            return ((ReferenceExpression) value).toJson();
        }
        if (value instanceof AspireUnion union) {
            return serializeValue(union.getValue());
        }
        if (value instanceof JsonSerializable jsonSerializable) {
            return jsonSerializable.toMap();
        }
        if (value instanceof Map) {
            @SuppressWarnings("unchecked")
            Map<String, Object> map = (Map<String, Object>) value;
            Map<String, Object> result = new HashMap<>();
            for (Map.Entry<String, Object> entry : map.entrySet()) {
                result.put(entry.getKey(), serializeValue(entry.getValue()));
            }
            return result;
        }
        if (value instanceof List) {
            @SuppressWarnings("unchecked")
            List<Object> list = (List<Object>) value;
            List<Object> result = new ArrayList<>();
            for (Object item : list) {
                result.add(serializeValue(item));
            }
            return result;
        }
        if (value.getClass().isArray()) {
            int length = java.lang.reflect.Array.getLength(value);
            List<Object> result = new ArrayList<>();
            for (int i = 0; i < length; i++) {
                result.add(serializeValue(java.lang.reflect.Array.get(value, i)));
            }
            return result;
        }
        if (value instanceof WireValueEnum wireValueEnum) {
            return wireValueEnum.getValue();
        }
        if (value instanceof Enum) {
            return ((Enum<?>) value).name();
        }
        return value;
    }

    // Simple JSON encoding
    private String toJson(Object value) {
        if (value == null) {
            return "null";
        }
        if (value instanceof String) {
            return "\"" + escapeJson((String) value) + "\"";
        }
        if (value instanceof Number || value instanceof Boolean) {
            return value.toString();
        }
        if (value instanceof Map) {
            @SuppressWarnings("unchecked")
            Map<String, Object> map = (Map<String, Object>) value;
            StringBuilder sb = new StringBuilder("{");
            boolean first = true;
            for (Map.Entry<String, Object> entry : map.entrySet()) {
                if (!first) sb.append(",");
                first = false;
                sb.append("\"").append(escapeJson(entry.getKey())).append("\":");
                sb.append(toJson(entry.getValue()));
            }
            sb.append("}");
            return sb.toString();
        }
        if (value instanceof List) {
            @SuppressWarnings("unchecked")
            List<Object> list = (List<Object>) value;
            StringBuilder sb = new StringBuilder("[");
            boolean first = true;
            for (Object item : list) {
                if (!first) sb.append(",");
                first = false;
                sb.append(toJson(item));
            }
            sb.append("]");
            return sb.toString();
        }
        if (value instanceof Object[]) {
            Object[] array = (Object[]) value;
            StringBuilder sb = new StringBuilder("[");
            boolean first = true;
            for (Object item : array) {
                if (!first) sb.append(",");
                first = false;
                sb.append(toJson(item));
            }
            sb.append("]");
            return sb.toString();
        }
        return "\"" + escapeJson(value.toString()) + "\"";
    }

    private String escapeJson(String s) {
        StringBuilder sb = new StringBuilder();
        for (char c : s.toCharArray()) {
            switch (c) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\b': sb.append("\\b"); break;
                case '\f': sb.append("\\f"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (c < ' ') {
                        sb.append(String.format("\\u%04x", (int) c));
                    } else {
                        sb.append(c);
                    }
            }
        }
        return sb.toString();
    }

    // Simple JSON parsing
    @SuppressWarnings("unchecked")
    private Object parseJson(String json) {
        return new JsonParser(json).parse();
    }

    private static class JsonParser {
        private final String json;
        private int pos = 0;

        JsonParser(String json) {
            this.json = json;
        }

        Object parse() {
            skipWhitespace();
            return parseValue();
        }

        private Object parseValue() {
            skipWhitespace();
            char c = peek();
            if (c == '{') return parseObject();
            if (c == '[') return parseArray();
            if (c == '"') return parseString();
            if (c == 't' || c == 'f') return parseBoolean();
            if (c == 'n') return parseNull();
            if (c == '-' || Character.isDigit(c)) return parseNumber();
            throw new RuntimeException("Unexpected character: " + c + " at position " + pos);
        }

        private Map<String, Object> parseObject() {
            expect('{');
            Map<String, Object> map = new LinkedHashMap<>();
            skipWhitespace();
            if (peek() != '}') {
                do {
                    skipWhitespace();
                    String key = parseString();
                    skipWhitespace();
                    expect(':');
                    Object value = parseValue();
                    map.put(key, value);
                    skipWhitespace();
                } while (tryConsume(','));
            }
            expect('}');
            return map;
        }

        private List<Object> parseArray() {
            expect('[');
            List<Object> list = new ArrayList<>();
            skipWhitespace();
            if (peek() != ']') {
                do {
                    list.add(parseValue());
                    skipWhitespace();
                } while (tryConsume(','));
            }
            expect(']');
            return list;
        }

        private String parseString() {
            expect('"');
            StringBuilder sb = new StringBuilder();
            while (pos < json.length()) {
                char c = json.charAt(pos++);
                if (c == '"') return sb.toString();
                if (c == '\\') {
                    c = json.charAt(pos++);
                    switch (c) {
                        case '"': case '\\': case '/': sb.append(c); break;
                        case 'b': sb.append('\b'); break;
                        case 'f': sb.append('\f'); break;
                        case 'n': sb.append('\n'); break;
                        case 'r': sb.append('\r'); break;
                        case 't': sb.append('\t'); break;
                        case 'u':
                            String hex = json.substring(pos, pos + 4);
                            sb.append((char) Integer.parseInt(hex, 16));
                            pos += 4;
                            break;
                    }
                } else {
                    sb.append(c);
                }
            }
            throw new RuntimeException("Unterminated string");
        }

        private Number parseNumber() {
            int start = pos;
            if (peek() == '-') pos++;
            while (pos < json.length() && Character.isDigit(json.charAt(pos))) pos++;
            if (pos < json.length() && json.charAt(pos) == '.') {
                pos++;
                while (pos < json.length() && Character.isDigit(json.charAt(pos))) pos++;
            }
            if (pos < json.length() && (json.charAt(pos) == 'e' || json.charAt(pos) == 'E')) {
                pos++;
                if (pos < json.length() && (json.charAt(pos) == '+' || json.charAt(pos) == '-')) pos++;
                while (pos < json.length() && Character.isDigit(json.charAt(pos))) pos++;
            }
            String numStr = json.substring(start, pos);
            if (numStr.contains(".") || numStr.contains("e") || numStr.contains("E")) {
                return new BigDecimal(numStr);
            }
            long l = Long.parseLong(numStr);
            if (l >= Integer.MIN_VALUE && l <= Integer.MAX_VALUE) {
                return (int) l;
            }
            return l;
        }

        private Boolean parseBoolean() {
            if (json.startsWith("true", pos)) {
                pos += 4;
                return true;
            }
            if (json.startsWith("false", pos)) {
                pos += 5;
                return false;
            }
            throw new RuntimeException("Expected boolean at position " + pos);
        }

        private Object parseNull() {
            if (json.startsWith("null", pos)) {
                pos += 4;
                return null;
            }
            throw new RuntimeException("Expected null at position " + pos);
        }

        private void skipWhitespace() {
            while (pos < json.length() && Character.isWhitespace(json.charAt(pos))) pos++;
        }

        private char peek() {
            return pos < json.length() ? json.charAt(pos) : '\0';
        }

        private void expect(char c) {
            skipWhitespace();
            if (pos >= json.length() || json.charAt(pos) != c) {
                throw new RuntimeException("Expected '" + c + "' at position " + pos);
            }
            pos++;
        }

        private boolean tryConsume(char c) {
            skipWhitespace();
            if (pos < json.length() && json.charAt(pos) == c) {
                pos++;
                return true;
            }
            return false;
        }
    }

    private void debug(String message) {
        if (DEBUG) {
            System.err.println("[Java ATS] " + message);
        }
    }

    private void debug(String message, Throwable error) {
        if (DEBUG) {
            System.err.println("[Java ATS] " + message);
            error.printStackTrace(System.err);
        }
    }
}
