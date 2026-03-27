// ===== Aspire.java =====
// Aspire.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Main entry point for Aspire SDK. */
public class Aspire {
    /** Connect to the AppHost server. */
    public static AspireClient connect() throws Exception {
        BaseRegistrations.ensureRegistered();
        AspireRegistrations.ensureRegistered();
        String socketPath = System.getenv("REMOTE_APP_HOST_SOCKET_PATH");
        if (socketPath == null || socketPath.isEmpty()) {
            throw new RuntimeException("REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`.");
        }
        AspireClient client = new AspireClient(socketPath);
        client.connect();
        client.onDisconnect(() -> System.exit(1));
        return client;
    }

    /** Create a new distributed application builder. */
    public static IDistributedApplicationBuilder createBuilder(CreateBuilderOptions options) throws Exception {
        AspireClient client = connect();
        Map<String, Object> resolvedOptions = new HashMap<>();
        if (options != null) {
            resolvedOptions.putAll(options.toMap());
        }
        if (resolvedOptions.get("Args") == null) {
            // Note: Java doesn't have easy access to command line args from here
            resolvedOptions.put("Args", new String[0]);
        }
        if (resolvedOptions.get("ProjectDirectory") == null) {
            resolvedOptions.put("ProjectDirectory", System.getProperty("user.dir"));
        }
        if (resolvedOptions.get("AppHostFilePath") == null) {
            String appHostFilePath = System.getenv("ASPIRE_APPHOST_FILEPATH");
            if (appHostFilePath != null && !appHostFilePath.isEmpty()) {
                resolvedOptions.put("AppHostFilePath", appHostFilePath);
            }
        }
        Map<String, Object> args = new HashMap<>();
        args.put("options", resolvedOptions);
        return (IDistributedApplicationBuilder) client.invokeCapability("Aspire.Hosting/createBuilderWithOptions", args);
    }
}

// ===== AspireAction0.java =====
// AspireAction0.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireAction0 {
    void invoke();
}

// ===== AspireAction1.java =====
// AspireAction1.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireAction1<T1> {
    void invoke(T1 arg1);
}

// ===== AspireAction2.java =====
// AspireAction2.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireAction2<T1, T2> {
    void invoke(T1 arg1, T2 arg2);
}

// ===== AspireAction3.java =====
// AspireAction3.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireAction3<T1, T2, T3> {
    void invoke(T1 arg1, T2 arg2, T3 arg3);
}

// ===== AspireAction4.java =====
// AspireAction4.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireAction4<T1, T2, T3, T4> {
    void invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
}

// ===== AspireClient.java =====
// AspireClient.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.function.*;

/**
 * AspireClient handles JSON-RPC communication with the AppHost server.
 */
public class AspireClient {
    private static final boolean DEBUG = System.getenv("ASPIRE_DEBUG") != null;
    
    private final String socketPath;
    private OutputStream outputStream;
    private InputStream inputStream;
    private final AtomicInteger requestId = new AtomicInteger(0);
    private final Map<String, Function<Object[], Object>> callbacks = new ConcurrentHashMap<>();
    private final Map<String, Consumer<Void>> cancellations = new ConcurrentHashMap<>();
    private Runnable disconnectHandler;
    private volatile boolean connected = false;

    // Handle wrapper factory registry
    private static final Map<String, BiFunction<Handle, AspireClient, Object>> handleWrappers = new ConcurrentHashMap<>();

    public static void registerHandleWrapper(String typeId, BiFunction<Handle, AspireClient, Object> factory) {
        handleWrappers.put(typeId, factory);
    }

    public AspireClient(String socketPath) {
        this.socketPath = socketPath;
    }

    public void connect() throws IOException {
        debug("Connecting to AppHost server at " + socketPath);
        
        if (isWindows()) {
            connectWindowsNamedPipe();
        } else {
            connectUnixSocket();
        }
        
        connected = true;
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
        RandomAccessFile pipe = new RandomAccessFile(pipePath, "rw");
        
        // Create streams from the RandomAccessFile
        FileDescriptor fd = pipe.getFD();
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
        this.disconnectHandler = handler;
    }

    public Object invokeCapability(String capabilityId, Map<String, Object> args) {
        int id = requestId.incrementAndGet();
        
        Map<String, Object> params = new HashMap<>();
        params.put("capabilityId", capabilityId);
        params.put("args", args);

        Map<String, Object> request = new HashMap<>();
        request.put("jsonrpc", "2.0");
        request.put("id", id);
        request.put("method", "invokeCapability");
        request.put("params", params);

        debug("Sending request invokeCapability with id=" + id);
        
        try {
            sendMessage(request);
            return readResponse(id);
        } catch (IOException e) {
            handleDisconnect();
            throw new RuntimeException("Failed to invoke capability: " + e.getMessage(), e);
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

    private Object readResponse(int expectedId) throws IOException {
        while (true) {
            Map<String, Object> message = readMessage();
            
            if (message.containsKey("method")) {
                // This is a request from server (callback invocation)
                handleServerRequest(message);
                continue;
            }
            
            // This is a response
            Object idObj = message.get("id");
            int responseId = idObj instanceof Number ? ((Number) idObj).intValue() : Integer.parseInt(idObj.toString());
            
            if (responseId != expectedId) {
                debug("Received response for different id: " + responseId + " (expected " + expectedId + ")");
                continue;
            }
            
            if (message.containsKey("error")) {
                @SuppressWarnings("unchecked")
                Map<String, Object> error = (Map<String, Object>) message.get("error");
                String code = String.valueOf(error.get("code"));
                String errorMessage = String.valueOf(error.get("message"));
                Object data = error.get("data");
                throw new CapabilityError(code, errorMessage, data);
            }
            
            Object result = message.get("result");
            return unwrapResult(result);
        }
    }

    @SuppressWarnings("unchecked")
    private Map<String, Object> readMessage() throws IOException {
        // Read headers
        StringBuilder headerBuilder = new StringBuilder();
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

    @SuppressWarnings("unchecked")
    private void handleServerRequest(Map<String, Object> request) throws IOException {
        String method = (String) request.get("method");
        Object idObj = request.get("id");
        Map<String, Object> params = (Map<String, Object>) request.get("params");

        debug("Received server request: " + method);

        Object result = null;
        Map<String, Object> error = null;

        try {
            if ("invokeCallback".equals(method)) {
                String callbackId = (String) params.get("callbackId");
                List<Object> args = (List<Object>) params.get("args");
                
                Function<Object[], Object> callback = callbacks.get(callbackId);
                if (callback != null) {
                    Object[] unwrappedArgs = args.stream()
                        .map(this::unwrapResult)
                        .toArray();
                    result = awaitValue(callback.apply(unwrappedArgs));
                } else {
                    error = createError(-32601, "Callback not found: " + callbackId);
                }
            } else if ("cancel".equals(method)) {
                String cancellationId = (String) params.get("cancellationId");
                Consumer<Void> handler = cancellations.get(cancellationId);
                if (handler != null) {
                    handler.accept(null);
                }
                result = true;
            } else {
                error = createError(-32601, "Unknown method: " + method);
            }
        } catch (Exception e) {
            error = createError(-32603, e.getMessage());
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
        connected = false;
        if (disconnectHandler != null) {
            disconnectHandler.run();
        }
    }

    public String registerCallback(Function<Object[], Object> callback) {
        String id = UUID.randomUUID().toString();
        callbacks.put(id, callback);
        return id;
    }

    public String registerCancellation(CancellationToken token) {
        String id = UUID.randomUUID().toString();
        cancellations.put(id, v -> token.cancel());
        return id;
    }

    public static Object awaitValue(Object value) {
        if (value instanceof CompletionStage<?> stage) {
            return stage.toCompletableFuture().join();
        }
        return value;
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
        if (value instanceof Object[]) {
            Object[] array = (Object[]) value;
            List<Object> result = new ArrayList<>();
            for (Object item : array) {
                result.add(serializeValue(item));
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
                return Double.parseDouble(numStr);
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
}

// ===== AspireDict.java =====
// AspireDict.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * AspireDict is a handle-backed dictionary with lazy handle resolution.
 */
public class AspireDict<K, V> extends HandleWrapperBase {
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

// ===== AspireFunc0.java =====
// AspireFunc0.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireFunc0<R> {
    R invoke();
}

// ===== AspireFunc1.java =====
// AspireFunc1.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireFunc1<T1, R> {
    R invoke(T1 arg1);
}

// ===== AspireFunc2.java =====
// AspireFunc2.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireFunc2<T1, T2, R> {
    R invoke(T1 arg1, T2 arg2);
}

// ===== AspireFunc3.java =====
// AspireFunc3.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireFunc3<T1, T2, T3, R> {
    R invoke(T1 arg1, T2 arg2, T3 arg3);
}

// ===== AspireFunc4.java =====
// AspireFunc4.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

@FunctionalInterface
public interface AspireFunc4<T1, T2, T3, T4, R> {
    R invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
}

// ===== AspireList.java =====
// AspireList.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * AspireList is a handle-backed list with lazy handle resolution.
 */
public class AspireList<T> extends HandleWrapperBase {
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

// ===== AspireRegistrations.java =====
// AspireRegistrations.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Static initializer to register handle wrappers. */
public class AspireRegistrations {
    static {
        AspireClient.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", (h, c) -> new TestCallbackContext(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext", (h, c) -> new TestResourceContext(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext", (h, c) -> new TestEnvironmentContext(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext", (h, c) -> new TestCollectionContext(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource", (h, c) -> new TestRedisResource(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource", (h, c) -> new TestDatabaseResource(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource", (h, c) -> new IResource(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString", (h, c) -> new IResourceWithConnectionString(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource", (h, c) -> new TestVaultResource(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource", (h, c) -> new ITestVaultResource(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", (h, c) -> new IDistributedApplicationBuilder(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment", (h, c) -> new IResourceWithEnvironment(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting/List<string>", (h, c) -> new AspireList(h, c));
        AspireClient.registerHandleWrapper("Aspire.Hosting/Dict<string,string>", (h, c) -> new AspireDict(h, c));
    }

    static void ensureRegistered() {
        // Called to trigger static initializer
    }
}

// ===== AspireUnion.java =====
// AspireUnion.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * Represents a runtime union value for generated Java APIs.
 */
public final class AspireUnion {
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

// ===== BaseRegistrations.java =====
// BaseRegistrations.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * Registers runtime-owned wrappers defined in Base.java.
 */
public final class BaseRegistrations {
    private BaseRegistrations() {
    }

    static {
        AspireClient.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", ReferenceExpression::new);
    }

    static void ensureRegistered() {
    }
}

// ===== CancellationToken.java =====
// CancellationToken.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.function.*;

/**
 * CancellationToken for cancelling operations.
 */
public class CancellationToken {
    private volatile boolean cancelled = false;
    private final List<Runnable> listeners = new CopyOnWriteArrayList<>();

    void cancel() {
        cancelled = true;
        for (Runnable listener : listeners) {
            listener.run();
        }
    }

    boolean isCancelled() { return cancelled; }

    void onCancel(Runnable listener) {
        listeners.add(listener);
        if (cancelled) {
            listener.run();
        }
    }
}

// ===== CapabilityError.java =====
// CapabilityError.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.function.*;

/**
 * CapabilityError represents an error from a capability invocation.
 */
public class CapabilityError extends RuntimeException {
    private final String code;
    private final Object data;

    CapabilityError(String code, String message, Object data) {
        super(message);
        this.code = code;
        this.data = data;
    }

    String getCode() { return code; }
    Object getData() { return data; }
}

// ===== Handle.java =====
// Handle.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.function.*;

/**
 * Handle represents a remote object reference.
 */
public class Handle {
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

// ===== HandleWrapperBase.java =====
// HandleWrapperBase.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * HandleWrapperBase is the base class for all handle wrappers.
 */
public class HandleWrapperBase {
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

// ===== IDistributedApplicationBuilder.java =====
// IDistributedApplicationBuilder.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder. */
public class IDistributedApplicationBuilder extends HandleWrapperBase {
    IDistributedApplicationBuilder(Handle handle, AspireClient client) {
        super(handle, client);
    }

    public TestRedisResource addTestRedis(String name) {
        return addTestRedis(name, null);
    }

    /** Adds a test Redis resource */
    public TestRedisResource addTestRedis(String name, Double port) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("name", AspireClient.serializeValue(name));
        if (port != null) {
            reqArgs.put("port", AspireClient.serializeValue(port));
        }
        return (TestRedisResource) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/addTestRedis", reqArgs);
    }

    /** Adds a test vault resource */
    public TestVaultResource addTestVault(String name) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("name", AspireClient.serializeValue(name));
        return (TestVaultResource) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/addTestVault", reqArgs);
    }

}

// ===== IResource.java =====
// IResource.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource. */
public class IResource extends ResourceBuilderBase {
    IResource(Handle handle, AspireClient client) {
        super(handle, client);
    }

}

// ===== IResourceWithConnectionString.java =====
// IResourceWithConnectionString.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString. */
public class IResourceWithConnectionString extends ResourceBuilderBase {
    IResourceWithConnectionString(Handle handle, AspireClient client) {
        super(handle, client);
    }

}

// ===== IResourceWithEnvironment.java =====
// IResourceWithEnvironment.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment. */
public class IResourceWithEnvironment extends HandleWrapperBase {
    IResourceWithEnvironment(Handle handle, AspireClient client) {
        super(handle, client);
    }

}

// ===== ITestVaultResource.java =====
// ITestVaultResource.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource. */
public class ITestVaultResource extends ResourceBuilderBase {
    ITestVaultResource(Handle handle, AspireClient client) {
        super(handle, client);
    }

}

// ===== ReferenceExpression.java =====
// ReferenceExpression.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * ReferenceExpression represents a reference expression.
 * Supports value mode (format + value providers), conditional mode, and handle mode.
 */
public class ReferenceExpression {
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

// ===== ResourceBuilderBase.java =====
// ResourceBuilderBase.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * ResourceBuilderBase extends HandleWrapperBase for resource builders.
 */
public class ResourceBuilderBase extends HandleWrapperBase {
    ResourceBuilderBase(Handle handle, AspireClient client) {
        super(handle, client);
    }
}

// ===== TestCallbackContext.java =====
// TestCallbackContext.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext. */
public class TestCallbackContext extends HandleWrapperBase {
    TestCallbackContext(Handle handle, AspireClient client) {
        super(handle, client);
    }

    /** Gets the Name property */
    public String name() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (String) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name", reqArgs);
    }

    /** Sets the Name property */
    public TestCallbackContext setName(String value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("value", AspireClient.serializeValue(value));
        return (TestCallbackContext) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName", reqArgs);
    }

    /** Gets the Value property */
    public double value() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (double) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value", reqArgs);
    }

    /** Sets the Value property */
    public TestCallbackContext setValue(double value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("value", AspireClient.serializeValue(value));
        return (TestCallbackContext) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue", reqArgs);
    }

    /** Gets the CancellationToken property */
    public CancellationToken cancellationToken() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (CancellationToken) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.cancellationToken", reqArgs);
    }

    /** Sets the CancellationToken property */
    public TestCallbackContext setCancellationToken(CancellationToken value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        if (value != null) {
            reqArgs.put("value", getClient().registerCancellation(value));
        }
        return (TestCallbackContext) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setCancellationToken", reqArgs);
    }

}

// ===== TestCollectionContext.java =====
// TestCollectionContext.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext. */
public class TestCollectionContext extends HandleWrapperBase {
    TestCollectionContext(Handle handle, AspireClient client) {
        super(handle, client);
    }

    /** Gets the Items property */
    private AspireList<String> itemsField;
    public AspireList<String> items() {
        if (itemsField == null) {
            itemsField = new AspireList<>(getHandle(), getClient(), "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items");
        }
        return itemsField;
    }

    /** Gets the Metadata property */
    private AspireDict<String, String> metadataField;
    public AspireDict<String, String> metadata() {
        if (metadataField == null) {
            metadataField = new AspireDict<>(getHandle(), getClient(), "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata");
        }
        return metadataField;
    }

}

// ===== TestConfigDto.java =====
// TestConfigDto.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** TestConfigDto DTO. */
public class TestConfigDto {
    private String name;
    private double port;
    private boolean enabled;
    private String optionalField;

    public String getName() { return name; }
    public void setName(String value) { this.name = value; }
    public double getPort() { return port; }
    public void setPort(double value) { this.port = value; }
    public boolean getEnabled() { return enabled; }
    public void setEnabled(boolean value) { this.enabled = value; }
    public String getOptionalField() { return optionalField; }
    public void setOptionalField(String value) { this.optionalField = value; }

    public Map<String, Object> toMap() {
        Map<String, Object> map = new HashMap<>();
        map.put("Name", AspireClient.serializeValue(name));
        map.put("Port", AspireClient.serializeValue(port));
        map.put("Enabled", AspireClient.serializeValue(enabled));
        map.put("OptionalField", AspireClient.serializeValue(optionalField));
        return map;
    }
}

// ===== TestDatabaseResource.java =====
// TestDatabaseResource.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource. */
public class TestDatabaseResource extends ResourceBuilderBase {
    TestDatabaseResource(Handle handle, AspireClient client) {
        super(handle, client);
    }

    /** Adds an optional string parameter */
    public TestDatabaseResource withOptionalString(WithOptionalStringOptions options) {
        var value = options == null ? null : options.getValue();
        var enabled = options == null ? null : options.getEnabled();
        return withOptionalStringImpl(value, enabled);
    }

    public TestDatabaseResource withOptionalString() {
        return withOptionalString(null);
    }

    /** Adds an optional string parameter */
    private TestDatabaseResource withOptionalStringImpl(String value, Boolean enabled) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        if (value != null) {
            reqArgs.put("value", AspireClient.serializeValue(value));
        }
        if (enabled != null) {
            reqArgs.put("enabled", AspireClient.serializeValue(enabled));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withOptionalString", reqArgs);
        return this;
    }

    /** Configures the resource with a DTO */
    public TestDatabaseResource withConfig(TestConfigDto config) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("config", AspireClient.serializeValue(config));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withConfig", reqArgs);
        return this;
    }

    /** Configures environment with callback (test version) */
    public TestDatabaseResource testWithEnvironmentCallback(AspireAction1<TestEnvironmentContext> callback) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var callbackId = getClient().registerCallback(args -> {
            var arg = (TestEnvironmentContext) args[0];
            callback.invoke(arg);
            return null;
        });
        if (callbackId != null) {
            reqArgs.put("callback", callbackId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/testWithEnvironmentCallback", reqArgs);
        return this;
    }

    /** Sets the created timestamp */
    public TestDatabaseResource withCreatedAt(String createdAt) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("createdAt", AspireClient.serializeValue(createdAt));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCreatedAt", reqArgs);
        return this;
    }

    /** Sets the modified timestamp */
    public TestDatabaseResource withModifiedAt(String modifiedAt) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("modifiedAt", AspireClient.serializeValue(modifiedAt));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withModifiedAt", reqArgs);
        return this;
    }

    /** Sets the correlation ID */
    public TestDatabaseResource withCorrelationId(String correlationId) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("correlationId", AspireClient.serializeValue(correlationId));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCorrelationId", reqArgs);
        return this;
    }

    public TestDatabaseResource withOptionalCallback() {
        return withOptionalCallback(null);
    }

    /** Configures with optional callback */
    public TestDatabaseResource withOptionalCallback(AspireAction1<TestCallbackContext> callback) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var callbackId = callback == null ? null : getClient().registerCallback(args -> {
            var arg = (TestCallbackContext) args[0];
            callback.invoke(arg);
            return null;
        });
        if (callbackId != null) {
            reqArgs.put("callback", callbackId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withOptionalCallback", reqArgs);
        return this;
    }

    /** Sets the resource status */
    public TestDatabaseResource withStatus(TestResourceStatus status) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("status", AspireClient.serializeValue(status));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withStatus", reqArgs);
        return this;
    }

    /** Configures with nested DTO */
    public TestDatabaseResource withNestedConfig(TestNestedDto config) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("config", AspireClient.serializeValue(config));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withNestedConfig", reqArgs);
        return this;
    }

    /** Adds validation callback */
    public TestDatabaseResource withValidator(AspireFunc1<TestResourceContext, Boolean> validator) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var validatorId = getClient().registerCallback(args -> {
            var arg = (TestResourceContext) args[0];
            return AspireClient.awaitValue(validator.invoke(arg));
        });
        if (validatorId != null) {
            reqArgs.put("validator", validatorId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withValidator", reqArgs);
        return this;
    }

    /** Waits for another resource (test version) */
    public TestDatabaseResource testWaitFor(IResource dependency) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("dependency", AspireClient.serializeValue(dependency));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/testWaitFor", reqArgs);
        return this;
    }

    public TestDatabaseResource testWaitFor(ResourceBuilderBase dependency) {
        return testWaitFor(new IResource(dependency.getHandle(), dependency.getClient()));
    }

    /** Adds a dependency on another resource */
    public TestDatabaseResource withDependency(IResourceWithConnectionString dependency) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("dependency", AspireClient.serializeValue(dependency));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withDependency", reqArgs);
        return this;
    }

    public TestDatabaseResource withDependency(ResourceBuilderBase dependency) {
        return withDependency(new IResourceWithConnectionString(dependency.getHandle(), dependency.getClient()));
    }

    /** Sets the endpoints */
    public TestDatabaseResource withEndpoints(String[] endpoints) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpoints", AspireClient.serializeValue(endpoints));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withEndpoints", reqArgs);
        return this;
    }

    /** Sets environment variables */
    public TestDatabaseResource withEnvironmentVariables(Map<String, String> variables) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("variables", AspireClient.serializeValue(variables));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withEnvironmentVariables", reqArgs);
        return this;
    }

    /** Performs a cancellable operation */
    public TestDatabaseResource withCancellableOperation(AspireAction1<CancellationToken> operation) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var operationId = getClient().registerCallback(args -> {
            var arg = CancellationToken.fromValue(args[0]);
            operation.invoke(arg);
            return null;
        });
        if (operationId != null) {
            reqArgs.put("operation", operationId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCancellableOperation", reqArgs);
        return this;
    }

    /** Adds a data volume */
    public TestDatabaseResource withDataVolume(WithDataVolumeOptions options) {
        var name = options == null ? null : options.getName();
        return withDataVolumeImpl(name);
    }

    public TestDatabaseResource withDataVolume() {
        return withDataVolume(null);
    }

    /** Adds a data volume */
    private TestDatabaseResource withDataVolumeImpl(String name) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        if (name != null) {
            reqArgs.put("name", AspireClient.serializeValue(name));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withDataVolume", reqArgs);
        return this;
    }

    /** Adds a label to the resource */
    public TestDatabaseResource withMergeLabel(String label) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("label", AspireClient.serializeValue(label));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLabel", reqArgs);
        return this;
    }

    /** Adds a categorized label to the resource */
    public TestDatabaseResource withMergeLabelCategorized(String label, String category) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("label", AspireClient.serializeValue(label));
        reqArgs.put("category", AspireClient.serializeValue(category));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLabelCategorized", reqArgs);
        return this;
    }

    /** Configures a named endpoint */
    public TestDatabaseResource withMergeEndpoint(String endpointName, double port) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpointName", AspireClient.serializeValue(endpointName));
        reqArgs.put("port", AspireClient.serializeValue(port));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeEndpoint", reqArgs);
        return this;
    }

    /** Configures a named endpoint with scheme */
    public TestDatabaseResource withMergeEndpointScheme(String endpointName, double port, String scheme) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpointName", AspireClient.serializeValue(endpointName));
        reqArgs.put("port", AspireClient.serializeValue(port));
        reqArgs.put("scheme", AspireClient.serializeValue(scheme));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeEndpointScheme", reqArgs);
        return this;
    }

    /** Configures resource logging */
    public TestDatabaseResource withMergeLogging(String logLevel, WithMergeLoggingOptions options) {
        var enableConsole = options == null ? null : options.getEnableConsole();
        var maxFiles = options == null ? null : options.getMaxFiles();
        return withMergeLoggingImpl(logLevel, enableConsole, maxFiles);
    }

    public TestDatabaseResource withMergeLogging(String logLevel) {
        return withMergeLogging(logLevel, null);
    }

    /** Configures resource logging */
    private TestDatabaseResource withMergeLoggingImpl(String logLevel, Boolean enableConsole, Double maxFiles) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("logLevel", AspireClient.serializeValue(logLevel));
        if (enableConsole != null) {
            reqArgs.put("enableConsole", AspireClient.serializeValue(enableConsole));
        }
        if (maxFiles != null) {
            reqArgs.put("maxFiles", AspireClient.serializeValue(maxFiles));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLogging", reqArgs);
        return this;
    }

    /** Configures resource logging with file path */
    public TestDatabaseResource withMergeLoggingPath(String logLevel, String logPath, WithMergeLoggingPathOptions options) {
        var enableConsole = options == null ? null : options.getEnableConsole();
        var maxFiles = options == null ? null : options.getMaxFiles();
        return withMergeLoggingPathImpl(logLevel, logPath, enableConsole, maxFiles);
    }

    public TestDatabaseResource withMergeLoggingPath(String logLevel, String logPath) {
        return withMergeLoggingPath(logLevel, logPath, null);
    }

    /** Configures resource logging with file path */
    private TestDatabaseResource withMergeLoggingPathImpl(String logLevel, String logPath, Boolean enableConsole, Double maxFiles) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("logLevel", AspireClient.serializeValue(logLevel));
        reqArgs.put("logPath", AspireClient.serializeValue(logPath));
        if (enableConsole != null) {
            reqArgs.put("enableConsole", AspireClient.serializeValue(enableConsole));
        }
        if (maxFiles != null) {
            reqArgs.put("maxFiles", AspireClient.serializeValue(maxFiles));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLoggingPath", reqArgs);
        return this;
    }

    /** Configures a route */
    public TestDatabaseResource withMergeRoute(String path, String method, String handler, double priority) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("path", AspireClient.serializeValue(path));
        reqArgs.put("method", AspireClient.serializeValue(method));
        reqArgs.put("handler", AspireClient.serializeValue(handler));
        reqArgs.put("priority", AspireClient.serializeValue(priority));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeRoute", reqArgs);
        return this;
    }

    /** Configures a route with middleware */
    public TestDatabaseResource withMergeRouteMiddleware(String path, String method, String handler, double priority, String middleware) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("path", AspireClient.serializeValue(path));
        reqArgs.put("method", AspireClient.serializeValue(method));
        reqArgs.put("handler", AspireClient.serializeValue(handler));
        reqArgs.put("priority", AspireClient.serializeValue(priority));
        reqArgs.put("middleware", AspireClient.serializeValue(middleware));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeRouteMiddleware", reqArgs);
        return this;
    }

}

// ===== TestDeeplyNestedDto.java =====
// TestDeeplyNestedDto.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** TestDeeplyNestedDto DTO. */
public class TestDeeplyNestedDto {
    private AspireDict<String, AspireList<TestConfigDto>> nestedData;
    private AspireDict<String, String>[] metadataArray;

    public AspireDict<String, AspireList<TestConfigDto>> getNestedData() { return nestedData; }
    public void setNestedData(AspireDict<String, AspireList<TestConfigDto>> value) { this.nestedData = value; }
    public AspireDict<String, String>[] getMetadataArray() { return metadataArray; }
    public void setMetadataArray(AspireDict<String, String>[] value) { this.metadataArray = value; }

    public Map<String, Object> toMap() {
        Map<String, Object> map = new HashMap<>();
        map.put("NestedData", AspireClient.serializeValue(nestedData));
        map.put("MetadataArray", AspireClient.serializeValue(metadataArray));
        return map;
    }
}

// ===== TestEnvironmentContext.java =====
// TestEnvironmentContext.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext. */
public class TestEnvironmentContext extends HandleWrapperBase {
    TestEnvironmentContext(Handle handle, AspireClient client) {
        super(handle, client);
    }

    /** Gets the Name property */
    public String name() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (String) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.name", reqArgs);
    }

    /** Sets the Name property */
    public TestEnvironmentContext setName(String value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("value", AspireClient.serializeValue(value));
        return (TestEnvironmentContext) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setName", reqArgs);
    }

    /** Gets the Description property */
    public String description() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (String) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description", reqArgs);
    }

    /** Sets the Description property */
    public TestEnvironmentContext setDescription(String value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("value", AspireClient.serializeValue(value));
        return (TestEnvironmentContext) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setDescription", reqArgs);
    }

    /** Gets the Priority property */
    public double priority() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (double) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.priority", reqArgs);
    }

    /** Sets the Priority property */
    public TestEnvironmentContext setPriority(double value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("value", AspireClient.serializeValue(value));
        return (TestEnvironmentContext) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setPriority", reqArgs);
    }

}

// ===== TestNestedDto.java =====
// TestNestedDto.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** TestNestedDto DTO. */
public class TestNestedDto {
    private String id;
    private TestConfigDto config;
    private AspireList<String> tags;
    private AspireDict<String, Double> counts;

    public String getId() { return id; }
    public void setId(String value) { this.id = value; }
    public TestConfigDto getConfig() { return config; }
    public void setConfig(TestConfigDto value) { this.config = value; }
    public AspireList<String> getTags() { return tags; }
    public void setTags(AspireList<String> value) { this.tags = value; }
    public AspireDict<String, Double> getCounts() { return counts; }
    public void setCounts(AspireDict<String, Double> value) { this.counts = value; }

    public Map<String, Object> toMap() {
        Map<String, Object> map = new HashMap<>();
        map.put("Id", AspireClient.serializeValue(id));
        map.put("Config", AspireClient.serializeValue(config));
        map.put("Tags", AspireClient.serializeValue(tags));
        map.put("Counts", AspireClient.serializeValue(counts));
        return map;
    }
}

// ===== TestPersistenceMode.java =====
// TestPersistenceMode.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** TestPersistenceMode enum. */
public enum TestPersistenceMode implements WireValueEnum {
    NONE("None"),
    VOLUME("Volume"),
    BIND("Bind");

    private final String value;

    TestPersistenceMode(String value) {
        this.value = value;
    }

    public String getValue() { return value; }

    public static TestPersistenceMode fromValue(String value) {
        for (TestPersistenceMode e : values()) {
            if (e.value.equals(value)) return e;
        }
        throw new IllegalArgumentException("Unknown value: " + value);
    }
}

// ===== TestRedisResource.java =====
// TestRedisResource.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource. */
public class TestRedisResource extends ResourceBuilderBase {
    TestRedisResource(Handle handle, AspireClient client) {
        super(handle, client);
    }

    public TestDatabaseResource addTestChildDatabase(String name) {
        return addTestChildDatabase(name, null);
    }

    /** Adds a child database to a test Redis resource */
    public TestDatabaseResource addTestChildDatabase(String name, String databaseName) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("name", AspireClient.serializeValue(name));
        if (databaseName != null) {
            reqArgs.put("databaseName", AspireClient.serializeValue(databaseName));
        }
        return (TestDatabaseResource) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/addTestChildDatabase", reqArgs);
    }

    public TestRedisResource withPersistence() {
        return withPersistence(null);
    }

    /** Configures the Redis resource with persistence */
    public TestRedisResource withPersistence(TestPersistenceMode mode) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        if (mode != null) {
            reqArgs.put("mode", AspireClient.serializeValue(mode));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withPersistence", reqArgs);
        return this;
    }

    /** Adds an optional string parameter */
    public TestRedisResource withOptionalString(WithOptionalStringOptions options) {
        var value = options == null ? null : options.getValue();
        var enabled = options == null ? null : options.getEnabled();
        return withOptionalStringImpl(value, enabled);
    }

    public TestRedisResource withOptionalString() {
        return withOptionalString(null);
    }

    /** Adds an optional string parameter */
    private TestRedisResource withOptionalStringImpl(String value, Boolean enabled) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        if (value != null) {
            reqArgs.put("value", AspireClient.serializeValue(value));
        }
        if (enabled != null) {
            reqArgs.put("enabled", AspireClient.serializeValue(enabled));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withOptionalString", reqArgs);
        return this;
    }

    /** Configures the resource with a DTO */
    public TestRedisResource withConfig(TestConfigDto config) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("config", AspireClient.serializeValue(config));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withConfig", reqArgs);
        return this;
    }

    /** Gets the tags for the resource */
    private AspireList<String> getTagsField;
    public AspireList<String> getTags() {
        if (getTagsField == null) {
            getTagsField = new AspireList<>(getHandle(), getClient(), "Aspire.Hosting.CodeGeneration.Java.Tests/getTags");
        }
        return getTagsField;
    }

    /** Gets the metadata for the resource */
    private AspireDict<String, String> getMetadataField;
    public AspireDict<String, String> getMetadata() {
        if (getMetadataField == null) {
            getMetadataField = new AspireDict<>(getHandle(), getClient(), "Aspire.Hosting.CodeGeneration.Java.Tests/getMetadata");
        }
        return getMetadataField;
    }

    /** Sets the connection string using a reference expression */
    public TestRedisResource withConnectionString(ReferenceExpression connectionString) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("connectionString", AspireClient.serializeValue(connectionString));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withConnectionString", reqArgs);
        return this;
    }

    /** Configures environment with callback (test version) */
    public TestRedisResource testWithEnvironmentCallback(AspireAction1<TestEnvironmentContext> callback) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var callbackId = getClient().registerCallback(args -> {
            var arg = (TestEnvironmentContext) args[0];
            callback.invoke(arg);
            return null;
        });
        if (callbackId != null) {
            reqArgs.put("callback", callbackId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/testWithEnvironmentCallback", reqArgs);
        return this;
    }

    /** Sets the created timestamp */
    public TestRedisResource withCreatedAt(String createdAt) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("createdAt", AspireClient.serializeValue(createdAt));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCreatedAt", reqArgs);
        return this;
    }

    /** Sets the modified timestamp */
    public TestRedisResource withModifiedAt(String modifiedAt) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("modifiedAt", AspireClient.serializeValue(modifiedAt));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withModifiedAt", reqArgs);
        return this;
    }

    /** Sets the correlation ID */
    public TestRedisResource withCorrelationId(String correlationId) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("correlationId", AspireClient.serializeValue(correlationId));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCorrelationId", reqArgs);
        return this;
    }

    public TestRedisResource withOptionalCallback() {
        return withOptionalCallback(null);
    }

    /** Configures with optional callback */
    public TestRedisResource withOptionalCallback(AspireAction1<TestCallbackContext> callback) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var callbackId = callback == null ? null : getClient().registerCallback(args -> {
            var arg = (TestCallbackContext) args[0];
            callback.invoke(arg);
            return null;
        });
        if (callbackId != null) {
            reqArgs.put("callback", callbackId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withOptionalCallback", reqArgs);
        return this;
    }

    /** Sets the resource status */
    public TestRedisResource withStatus(TestResourceStatus status) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("status", AspireClient.serializeValue(status));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withStatus", reqArgs);
        return this;
    }

    /** Configures with nested DTO */
    public TestRedisResource withNestedConfig(TestNestedDto config) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("config", AspireClient.serializeValue(config));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withNestedConfig", reqArgs);
        return this;
    }

    /** Adds validation callback */
    public TestRedisResource withValidator(AspireFunc1<TestResourceContext, Boolean> validator) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var validatorId = getClient().registerCallback(args -> {
            var arg = (TestResourceContext) args[0];
            return AspireClient.awaitValue(validator.invoke(arg));
        });
        if (validatorId != null) {
            reqArgs.put("validator", validatorId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withValidator", reqArgs);
        return this;
    }

    /** Waits for another resource (test version) */
    public TestRedisResource testWaitFor(IResource dependency) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("dependency", AspireClient.serializeValue(dependency));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/testWaitFor", reqArgs);
        return this;
    }

    public TestRedisResource testWaitFor(ResourceBuilderBase dependency) {
        return testWaitFor(new IResource(dependency.getHandle(), dependency.getClient()));
    }

    /** Gets the endpoints */
    public String[] getEndpoints() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        return (String[]) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/getEndpoints", reqArgs);
    }

    /** Sets connection string using direct interface target */
    public TestRedisResource withConnectionStringDirect(String connectionString) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("connectionString", AspireClient.serializeValue(connectionString));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withConnectionStringDirect", reqArgs);
        return this;
    }

    /** Redis-specific configuration */
    public TestRedisResource withRedisSpecific(String option) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("option", AspireClient.serializeValue(option));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withRedisSpecific", reqArgs);
        return this;
    }

    /** Adds a dependency on another resource */
    public TestRedisResource withDependency(IResourceWithConnectionString dependency) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("dependency", AspireClient.serializeValue(dependency));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withDependency", reqArgs);
        return this;
    }

    public TestRedisResource withDependency(ResourceBuilderBase dependency) {
        return withDependency(new IResourceWithConnectionString(dependency.getHandle(), dependency.getClient()));
    }

    /** Sets the endpoints */
    public TestRedisResource withEndpoints(String[] endpoints) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpoints", AspireClient.serializeValue(endpoints));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withEndpoints", reqArgs);
        return this;
    }

    /** Sets environment variables */
    public TestRedisResource withEnvironmentVariables(Map<String, String> variables) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("variables", AspireClient.serializeValue(variables));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withEnvironmentVariables", reqArgs);
        return this;
    }

    public String getStatusAsync() {
        return getStatusAsync(null);
    }

    /** Gets the status of the resource asynchronously */
    public String getStatusAsync(CancellationToken cancellationToken) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        if (cancellationToken != null) {
            reqArgs.put("cancellationToken", getClient().registerCancellation(cancellationToken));
        }
        return (String) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/getStatusAsync", reqArgs);
    }

    /** Performs a cancellable operation */
    public TestRedisResource withCancellableOperation(AspireAction1<CancellationToken> operation) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var operationId = getClient().registerCallback(args -> {
            var arg = CancellationToken.fromValue(args[0]);
            operation.invoke(arg);
            return null;
        });
        if (operationId != null) {
            reqArgs.put("operation", operationId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCancellableOperation", reqArgs);
        return this;
    }

    public boolean waitForReadyAsync(double timeout) {
        return waitForReadyAsync(timeout, null);
    }

    /** Waits for the resource to be ready */
    public boolean waitForReadyAsync(double timeout, CancellationToken cancellationToken) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("timeout", AspireClient.serializeValue(timeout));
        if (cancellationToken != null) {
            reqArgs.put("cancellationToken", getClient().registerCancellation(cancellationToken));
        }
        return (boolean) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/waitForReadyAsync", reqArgs);
    }

    /** Tests multi-param callback destructuring */
    public TestRedisResource withMultiParamHandleCallback(AspireAction2<TestCallbackContext, TestEnvironmentContext> callback) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var callbackId = getClient().registerCallback(args -> {
            var arg1 = (TestCallbackContext) args[0];
            var arg2 = (TestEnvironmentContext) args[1];
            callback.invoke(arg1, arg2);
            return null;
        });
        if (callbackId != null) {
            reqArgs.put("callback", callbackId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMultiParamHandleCallback", reqArgs);
        return this;
    }

    /** Adds a data volume with persistence */
    public TestRedisResource withDataVolume(WithDataVolumeOptions options) {
        var name = options == null ? null : options.getName();
        var isReadOnly = options == null ? null : options.isReadOnly();
        return withDataVolumeImpl(name, isReadOnly);
    }

    public TestRedisResource withDataVolume() {
        return withDataVolume(null);
    }

    /** Adds a data volume with persistence */
    private TestRedisResource withDataVolumeImpl(String name, Boolean isReadOnly) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        if (name != null) {
            reqArgs.put("name", AspireClient.serializeValue(name));
        }
        if (isReadOnly != null) {
            reqArgs.put("isReadOnly", AspireClient.serializeValue(isReadOnly));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withDataVolume", reqArgs);
        return this;
    }

    /** Adds a label to the resource */
    public TestRedisResource withMergeLabel(String label) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("label", AspireClient.serializeValue(label));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLabel", reqArgs);
        return this;
    }

    /** Adds a categorized label to the resource */
    public TestRedisResource withMergeLabelCategorized(String label, String category) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("label", AspireClient.serializeValue(label));
        reqArgs.put("category", AspireClient.serializeValue(category));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLabelCategorized", reqArgs);
        return this;
    }

    /** Configures a named endpoint */
    public TestRedisResource withMergeEndpoint(String endpointName, double port) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpointName", AspireClient.serializeValue(endpointName));
        reqArgs.put("port", AspireClient.serializeValue(port));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeEndpoint", reqArgs);
        return this;
    }

    /** Configures a named endpoint with scheme */
    public TestRedisResource withMergeEndpointScheme(String endpointName, double port, String scheme) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpointName", AspireClient.serializeValue(endpointName));
        reqArgs.put("port", AspireClient.serializeValue(port));
        reqArgs.put("scheme", AspireClient.serializeValue(scheme));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeEndpointScheme", reqArgs);
        return this;
    }

    /** Configures resource logging */
    public TestRedisResource withMergeLogging(String logLevel, WithMergeLoggingOptions options) {
        var enableConsole = options == null ? null : options.getEnableConsole();
        var maxFiles = options == null ? null : options.getMaxFiles();
        return withMergeLoggingImpl(logLevel, enableConsole, maxFiles);
    }

    public TestRedisResource withMergeLogging(String logLevel) {
        return withMergeLogging(logLevel, null);
    }

    /** Configures resource logging */
    private TestRedisResource withMergeLoggingImpl(String logLevel, Boolean enableConsole, Double maxFiles) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("logLevel", AspireClient.serializeValue(logLevel));
        if (enableConsole != null) {
            reqArgs.put("enableConsole", AspireClient.serializeValue(enableConsole));
        }
        if (maxFiles != null) {
            reqArgs.put("maxFiles", AspireClient.serializeValue(maxFiles));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLogging", reqArgs);
        return this;
    }

    /** Configures resource logging with file path */
    public TestRedisResource withMergeLoggingPath(String logLevel, String logPath, WithMergeLoggingPathOptions options) {
        var enableConsole = options == null ? null : options.getEnableConsole();
        var maxFiles = options == null ? null : options.getMaxFiles();
        return withMergeLoggingPathImpl(logLevel, logPath, enableConsole, maxFiles);
    }

    public TestRedisResource withMergeLoggingPath(String logLevel, String logPath) {
        return withMergeLoggingPath(logLevel, logPath, null);
    }

    /** Configures resource logging with file path */
    private TestRedisResource withMergeLoggingPathImpl(String logLevel, String logPath, Boolean enableConsole, Double maxFiles) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("logLevel", AspireClient.serializeValue(logLevel));
        reqArgs.put("logPath", AspireClient.serializeValue(logPath));
        if (enableConsole != null) {
            reqArgs.put("enableConsole", AspireClient.serializeValue(enableConsole));
        }
        if (maxFiles != null) {
            reqArgs.put("maxFiles", AspireClient.serializeValue(maxFiles));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLoggingPath", reqArgs);
        return this;
    }

    /** Configures a route */
    public TestRedisResource withMergeRoute(String path, String method, String handler, double priority) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("path", AspireClient.serializeValue(path));
        reqArgs.put("method", AspireClient.serializeValue(method));
        reqArgs.put("handler", AspireClient.serializeValue(handler));
        reqArgs.put("priority", AspireClient.serializeValue(priority));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeRoute", reqArgs);
        return this;
    }

    /** Configures a route with middleware */
    public TestRedisResource withMergeRouteMiddleware(String path, String method, String handler, double priority, String middleware) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("path", AspireClient.serializeValue(path));
        reqArgs.put("method", AspireClient.serializeValue(method));
        reqArgs.put("handler", AspireClient.serializeValue(handler));
        reqArgs.put("priority", AspireClient.serializeValue(priority));
        reqArgs.put("middleware", AspireClient.serializeValue(middleware));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeRouteMiddleware", reqArgs);
        return this;
    }

}

// ===== TestResourceContext.java =====
// TestResourceContext.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext. */
public class TestResourceContext extends HandleWrapperBase {
    TestResourceContext(Handle handle, AspireClient client) {
        super(handle, client);
    }

    /** Gets the Name property */
    public String name() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (String) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.name", reqArgs);
    }

    /** Sets the Name property */
    public TestResourceContext setName(String value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("value", AspireClient.serializeValue(value));
        return (TestResourceContext) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setName", reqArgs);
    }

    /** Gets the Value property */
    public double value() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (double) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.value", reqArgs);
    }

    /** Sets the Value property */
    public TestResourceContext setValue(double value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("value", AspireClient.serializeValue(value));
        return (TestResourceContext) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValue", reqArgs);
    }

    /** Invokes the GetValueAsync method */
    public String getValueAsync() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (String) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync", reqArgs);
    }

    /** Invokes the SetValueAsync method */
    public void setValueAsync(String value) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        reqArgs.put("value", AspireClient.serializeValue(value));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValueAsync", reqArgs);
    }

    /** Invokes the ValidateAsync method */
    public boolean validateAsync() {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("context", AspireClient.serializeValue(getHandle()));
        return (boolean) getClient().invokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.validateAsync", reqArgs);
    }

}

// ===== TestResourceStatus.java =====
// TestResourceStatus.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** TestResourceStatus enum. */
public enum TestResourceStatus implements WireValueEnum {
    PENDING("Pending"),
    RUNNING("Running"),
    STOPPED("Stopped"),
    FAILED("Failed");

    private final String value;

    TestResourceStatus(String value) {
        this.value = value;
    }

    public String getValue() { return value; }

    public static TestResourceStatus fromValue(String value) {
        for (TestResourceStatus e : values()) {
            if (e.value.equals(value)) return e;
        }
        throw new IllegalArgumentException("Unknown value: " + value);
    }
}

// ===== TestVaultResource.java =====
// TestVaultResource.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Wrapper for Aspire.Hosting.CodeGeneration.Java.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource. */
public class TestVaultResource extends ResourceBuilderBase {
    TestVaultResource(Handle handle, AspireClient client) {
        super(handle, client);
    }

    /** Adds an optional string parameter */
    public TestVaultResource withOptionalString(WithOptionalStringOptions options) {
        var value = options == null ? null : options.getValue();
        var enabled = options == null ? null : options.getEnabled();
        return withOptionalStringImpl(value, enabled);
    }

    public TestVaultResource withOptionalString() {
        return withOptionalString(null);
    }

    /** Adds an optional string parameter */
    private TestVaultResource withOptionalStringImpl(String value, Boolean enabled) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        if (value != null) {
            reqArgs.put("value", AspireClient.serializeValue(value));
        }
        if (enabled != null) {
            reqArgs.put("enabled", AspireClient.serializeValue(enabled));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withOptionalString", reqArgs);
        return this;
    }

    /** Configures the resource with a DTO */
    public TestVaultResource withConfig(TestConfigDto config) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("config", AspireClient.serializeValue(config));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withConfig", reqArgs);
        return this;
    }

    /** Configures environment with callback (test version) */
    public TestVaultResource testWithEnvironmentCallback(AspireAction1<TestEnvironmentContext> callback) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var callbackId = getClient().registerCallback(args -> {
            var arg = (TestEnvironmentContext) args[0];
            callback.invoke(arg);
            return null;
        });
        if (callbackId != null) {
            reqArgs.put("callback", callbackId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/testWithEnvironmentCallback", reqArgs);
        return this;
    }

    /** Sets the created timestamp */
    public TestVaultResource withCreatedAt(String createdAt) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("createdAt", AspireClient.serializeValue(createdAt));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCreatedAt", reqArgs);
        return this;
    }

    /** Sets the modified timestamp */
    public TestVaultResource withModifiedAt(String modifiedAt) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("modifiedAt", AspireClient.serializeValue(modifiedAt));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withModifiedAt", reqArgs);
        return this;
    }

    /** Sets the correlation ID */
    public TestVaultResource withCorrelationId(String correlationId) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("correlationId", AspireClient.serializeValue(correlationId));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCorrelationId", reqArgs);
        return this;
    }

    public TestVaultResource withOptionalCallback() {
        return withOptionalCallback(null);
    }

    /** Configures with optional callback */
    public TestVaultResource withOptionalCallback(AspireAction1<TestCallbackContext> callback) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var callbackId = callback == null ? null : getClient().registerCallback(args -> {
            var arg = (TestCallbackContext) args[0];
            callback.invoke(arg);
            return null;
        });
        if (callbackId != null) {
            reqArgs.put("callback", callbackId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withOptionalCallback", reqArgs);
        return this;
    }

    /** Sets the resource status */
    public TestVaultResource withStatus(TestResourceStatus status) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("status", AspireClient.serializeValue(status));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withStatus", reqArgs);
        return this;
    }

    /** Configures with nested DTO */
    public TestVaultResource withNestedConfig(TestNestedDto config) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("config", AspireClient.serializeValue(config));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withNestedConfig", reqArgs);
        return this;
    }

    /** Adds validation callback */
    public TestVaultResource withValidator(AspireFunc1<TestResourceContext, Boolean> validator) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var validatorId = getClient().registerCallback(args -> {
            var arg = (TestResourceContext) args[0];
            return AspireClient.awaitValue(validator.invoke(arg));
        });
        if (validatorId != null) {
            reqArgs.put("validator", validatorId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withValidator", reqArgs);
        return this;
    }

    /** Waits for another resource (test version) */
    public TestVaultResource testWaitFor(IResource dependency) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("dependency", AspireClient.serializeValue(dependency));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/testWaitFor", reqArgs);
        return this;
    }

    public TestVaultResource testWaitFor(ResourceBuilderBase dependency) {
        return testWaitFor(new IResource(dependency.getHandle(), dependency.getClient()));
    }

    /** Adds a dependency on another resource */
    public TestVaultResource withDependency(IResourceWithConnectionString dependency) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("dependency", AspireClient.serializeValue(dependency));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withDependency", reqArgs);
        return this;
    }

    public TestVaultResource withDependency(ResourceBuilderBase dependency) {
        return withDependency(new IResourceWithConnectionString(dependency.getHandle(), dependency.getClient()));
    }

    /** Sets the endpoints */
    public TestVaultResource withEndpoints(String[] endpoints) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpoints", AspireClient.serializeValue(endpoints));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withEndpoints", reqArgs);
        return this;
    }

    /** Sets environment variables */
    public TestVaultResource withEnvironmentVariables(Map<String, String> variables) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("variables", AspireClient.serializeValue(variables));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withEnvironmentVariables", reqArgs);
        return this;
    }

    /** Performs a cancellable operation */
    public TestVaultResource withCancellableOperation(AspireAction1<CancellationToken> operation) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        var operationId = getClient().registerCallback(args -> {
            var arg = CancellationToken.fromValue(args[0]);
            operation.invoke(arg);
            return null;
        });
        if (operationId != null) {
            reqArgs.put("operation", operationId);
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withCancellableOperation", reqArgs);
        return this;
    }

    /** Configures vault using direct interface target */
    public TestVaultResource withVaultDirect(String option) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("option", AspireClient.serializeValue(option));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withVaultDirect", reqArgs);
        return this;
    }

    /** Adds a label to the resource */
    public TestVaultResource withMergeLabel(String label) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("label", AspireClient.serializeValue(label));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLabel", reqArgs);
        return this;
    }

    /** Adds a categorized label to the resource */
    public TestVaultResource withMergeLabelCategorized(String label, String category) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("label", AspireClient.serializeValue(label));
        reqArgs.put("category", AspireClient.serializeValue(category));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLabelCategorized", reqArgs);
        return this;
    }

    /** Configures a named endpoint */
    public TestVaultResource withMergeEndpoint(String endpointName, double port) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpointName", AspireClient.serializeValue(endpointName));
        reqArgs.put("port", AspireClient.serializeValue(port));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeEndpoint", reqArgs);
        return this;
    }

    /** Configures a named endpoint with scheme */
    public TestVaultResource withMergeEndpointScheme(String endpointName, double port, String scheme) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("endpointName", AspireClient.serializeValue(endpointName));
        reqArgs.put("port", AspireClient.serializeValue(port));
        reqArgs.put("scheme", AspireClient.serializeValue(scheme));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeEndpointScheme", reqArgs);
        return this;
    }

    /** Configures resource logging */
    public TestVaultResource withMergeLogging(String logLevel, WithMergeLoggingOptions options) {
        var enableConsole = options == null ? null : options.getEnableConsole();
        var maxFiles = options == null ? null : options.getMaxFiles();
        return withMergeLoggingImpl(logLevel, enableConsole, maxFiles);
    }

    public TestVaultResource withMergeLogging(String logLevel) {
        return withMergeLogging(logLevel, null);
    }

    /** Configures resource logging */
    private TestVaultResource withMergeLoggingImpl(String logLevel, Boolean enableConsole, Double maxFiles) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("logLevel", AspireClient.serializeValue(logLevel));
        if (enableConsole != null) {
            reqArgs.put("enableConsole", AspireClient.serializeValue(enableConsole));
        }
        if (maxFiles != null) {
            reqArgs.put("maxFiles", AspireClient.serializeValue(maxFiles));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLogging", reqArgs);
        return this;
    }

    /** Configures resource logging with file path */
    public TestVaultResource withMergeLoggingPath(String logLevel, String logPath, WithMergeLoggingPathOptions options) {
        var enableConsole = options == null ? null : options.getEnableConsole();
        var maxFiles = options == null ? null : options.getMaxFiles();
        return withMergeLoggingPathImpl(logLevel, logPath, enableConsole, maxFiles);
    }

    public TestVaultResource withMergeLoggingPath(String logLevel, String logPath) {
        return withMergeLoggingPath(logLevel, logPath, null);
    }

    /** Configures resource logging with file path */
    private TestVaultResource withMergeLoggingPathImpl(String logLevel, String logPath, Boolean enableConsole, Double maxFiles) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("logLevel", AspireClient.serializeValue(logLevel));
        reqArgs.put("logPath", AspireClient.serializeValue(logPath));
        if (enableConsole != null) {
            reqArgs.put("enableConsole", AspireClient.serializeValue(enableConsole));
        }
        if (maxFiles != null) {
            reqArgs.put("maxFiles", AspireClient.serializeValue(maxFiles));
        }
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeLoggingPath", reqArgs);
        return this;
    }

    /** Configures a route */
    public TestVaultResource withMergeRoute(String path, String method, String handler, double priority) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("path", AspireClient.serializeValue(path));
        reqArgs.put("method", AspireClient.serializeValue(method));
        reqArgs.put("handler", AspireClient.serializeValue(handler));
        reqArgs.put("priority", AspireClient.serializeValue(priority));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeRoute", reqArgs);
        return this;
    }

    /** Configures a route with middleware */
    public TestVaultResource withMergeRouteMiddleware(String path, String method, String handler, double priority, String middleware) {
        Map<String, Object> reqArgs = new HashMap<>();
        reqArgs.put("builder", AspireClient.serializeValue(getHandle()));
        reqArgs.put("path", AspireClient.serializeValue(path));
        reqArgs.put("method", AspireClient.serializeValue(method));
        reqArgs.put("handler", AspireClient.serializeValue(handler));
        reqArgs.put("priority", AspireClient.serializeValue(priority));
        reqArgs.put("middleware", AspireClient.serializeValue(middleware));
        getClient().invokeCapability("Aspire.Hosting.CodeGeneration.Java.Tests/withMergeRouteMiddleware", reqArgs);
        return this;
    }

}

// ===== WireValueEnum.java =====
// WireValueEnum.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;

/**
 * Marker interface for generated enums that need a transport value distinct from Enum.name().
 */
public interface WireValueEnum {
    String getValue();
}

// ===== WithDataVolumeOptions.java =====
// WithDataVolumeOptions.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Options for WithDataVolume. */
public final class WithDataVolumeOptions {
    private String name;
    private Boolean isReadOnly;

    public String getName() { return name; }
    public WithDataVolumeOptions name(String value) {
        this.name = value;
        return this;
    }

    public Boolean isReadOnly() { return isReadOnly; }
    public WithDataVolumeOptions isReadOnly(Boolean value) {
        this.isReadOnly = value;
        return this;
    }

}

// ===== WithMergeLoggingOptions.java =====
// WithMergeLoggingOptions.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Options for WithMergeLogging. */
public final class WithMergeLoggingOptions {
    private Boolean enableConsole;
    private Double maxFiles;

    public Boolean getEnableConsole() { return enableConsole; }
    public WithMergeLoggingOptions enableConsole(Boolean value) {
        this.enableConsole = value;
        return this;
    }

    public Double getMaxFiles() { return maxFiles; }
    public WithMergeLoggingOptions maxFiles(Double value) {
        this.maxFiles = value;
        return this;
    }

}

// ===== WithMergeLoggingPathOptions.java =====
// WithMergeLoggingPathOptions.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Options for WithMergeLoggingPath. */
public final class WithMergeLoggingPathOptions {
    private Boolean enableConsole;
    private Double maxFiles;

    public Boolean getEnableConsole() { return enableConsole; }
    public WithMergeLoggingPathOptions enableConsole(Boolean value) {
        this.enableConsole = value;
        return this;
    }

    public Double getMaxFiles() { return maxFiles; }
    public WithMergeLoggingPathOptions maxFiles(Double value) {
        this.maxFiles = value;
        return this;
    }

}

// ===== WithOptionalStringOptions.java =====
// WithOptionalStringOptions.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.*;
import java.util.function.*;

/** Options for WithOptionalString. */
public final class WithOptionalStringOptions {
    private String value;
    private Boolean enabled;

    public String getValue() { return value; }
    public WithOptionalStringOptions value(String value) {
        this.value = value;
        return this;
    }

    public Boolean getEnabled() { return enabled; }
    public WithOptionalStringOptions enabled(Boolean value) {
        this.enabled = value;
        return this;
    }

}

// ===== sources.txt =====
.modules/Aspire.java
.modules/AspireAction0.java
.modules/AspireAction1.java
.modules/AspireAction2.java
.modules/AspireAction3.java
.modules/AspireAction4.java
.modules/AspireClient.java
.modules/AspireDict.java
.modules/AspireFunc0.java
.modules/AspireFunc1.java
.modules/AspireFunc2.java
.modules/AspireFunc3.java
.modules/AspireFunc4.java
.modules/AspireList.java
.modules/AspireRegistrations.java
.modules/AspireUnion.java
.modules/BaseRegistrations.java
.modules/CancellationToken.java
.modules/CapabilityError.java
.modules/Handle.java
.modules/HandleWrapperBase.java
.modules/IDistributedApplicationBuilder.java
.modules/IResource.java
.modules/IResourceWithConnectionString.java
.modules/IResourceWithEnvironment.java
.modules/ITestVaultResource.java
.modules/ReferenceExpression.java
.modules/ResourceBuilderBase.java
.modules/TestCallbackContext.java
.modules/TestCollectionContext.java
.modules/TestConfigDto.java
.modules/TestDatabaseResource.java
.modules/TestDeeplyNestedDto.java
.modules/TestEnvironmentContext.java
.modules/TestNestedDto.java
.modules/TestPersistenceMode.java
.modules/TestRedisResource.java
.modules/TestResourceContext.java
.modules/TestResourceStatus.java
.modules/TestVaultResource.java
.modules/WireValueEnum.java
.modules/WithDataVolumeOptions.java
.modules/WithMergeLoggingOptions.java
.modules/WithMergeLoggingPathOptions.java
.modules/WithOptionalStringOptions.java
