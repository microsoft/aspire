// Transport.java - JSON-RPC transport layer for Aspire Java SDK
// GENERATED CODE - DO NOT EDIT

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

    String getCode() { return code; }
    Object getData() { return data; }
}

/**
 * CancellationToken for cancelling operations.
 */
class CancellationToken {
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

interface JsonSerializable {
    Map<String, Object> toMap();
}

/**
 * AspireClient handles JSON-RPC communication with the AppHost server.
 */
class AspireClient {
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

    public void authenticate(String token) {
        int id = requestId.incrementAndGet();

        List<Object> params = List.of(token);

        Map<String, Object> request = new HashMap<>();
        request.put("jsonrpc", "2.0");
        request.put("id", id);
        request.put("method", "authenticate");
        request.put("params", params);

        debug("Sending request authenticate with id=" + id);

        try {
            sendMessage(request);
            Object result = readResponse(id);
            if (!(result instanceof Boolean authenticated) || !authenticated) {
                throw new RuntimeException("Failed to authenticate to the AppHost server.");
            }
        } catch (IOException e) {
            handleDisconnect();
            throw new RuntimeException("Failed to authenticate: " + e.getMessage(), e);
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
                        result = awaitValue(callback.apply(unwrappedArgs));
                    } else {
                        error = createError(-32601, "Callback not found: " + callbackId);
                    }
                }
            } else if ("cancel".equals(method)) {
                String cancellationId = getCancellationId(params);
                if (cancellationId == null) {
                    error = createError(-32602, "Invalid params: cancellationId is required.");
                } else {
                    Consumer<Void> handler = cancellations.get(cancellationId);
                    if (handler != null) {
                        handler.accept(null);
                    }
                    result = true;
                }
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
