#   -------------------------------------------------------------
#   Copyright (c) Microsoft Corporation. All rights reserved.
#   Licensed under the MIT License. See LICENSE in project root for information.
#
#   This is a generated file. Any modifications may be overwritten.
#   -------------------------------------------------------------

from __future__ import annotations

import os
import base64
import sys
import json
import logging
import secrets
import signal
import socket
import threading
import time
import abc
import datetime
import types
import typing
from functools import cached_property as _cached_property
from contextlib import AbstractContextManager

_logger = logging.getLogger(__name__)

# Maximum allowed message size (64 MB) to prevent memory exhaustion from malicious Content-Length
_MAX_MESSAGE_SIZE = 64 * 1024 * 1024

# Maximum number of headers and total header size to prevent header-flooding attacks
_MAX_HEADER_COUNT = 16
_MAX_HEADER_BYTES = 8 * 1024

# Marker string for detecting generic .NET builder type names.
_BUILDER_GENERIC_MARKER = "Builder`1["

_uncached_property = property

__version__ = "0.1.0"


# ============================================================================
# Platform-specific Socket Implementation
# ============================================================================

if sys.platform == "win32":
    import ctypes
    from ctypes import wintypes

    _kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)

    class _OVERLAPPED(ctypes.Structure):
        '''Windows OVERLAPPED structure for async I/O.'''
        _fields_ = [
            ("Internal", ctypes.POINTER(ctypes.c_ulong)),
            ("InternalHigh", ctypes.POINTER(ctypes.c_ulong)),
            ("Offset", wintypes.DWORD),
            ("OffsetHigh", wintypes.DWORD),
            ("hEvent", wintypes.HANDLE),
        ]

    class _PipeSocket:
        '''A socket-like wrapper around a Win32 named pipe using ctypes.'''

        # Win32 constants
        GENERIC_READ = 0x80000000
        GENERIC_WRITE = 0x40000000
        OPEN_EXISTING = 3
        INVALID_HANDLE_VALUE = -1
        FILE_FLAG_OVERLAPPED = 0x40000000
        ERROR_IO_PENDING = 997
        ERROR_FILE_NOT_FOUND = 2

        def __init__(self, pipe_path: str) -> None:
            self._handle: int | None = None
            handle = _kernel32.CreateFileW(
                pipe_path,
                self.GENERIC_READ | self.GENERIC_WRITE,
                0,  # no sharing
                None,  # default security
                self.OPEN_EXISTING,
                self.FILE_FLAG_OVERLAPPED,  # match server's async mode
                None  # no template
            )

            if handle == self.INVALID_HANDLE_VALUE:
                error = ctypes.get_last_error()
                if error == self.ERROR_FILE_NOT_FOUND:
                    raise FileNotFoundError(f"Pipe not found: {pipe_path}")
                raise OSError(f"CreateFile failed with error {error}")

            self._handle = handle

        def _create_overlapped_event(self) -> _OVERLAPPED:
            '''Create an OVERLAPPED structure with an event for async I/O.'''
            overlapped = _OVERLAPPED()
            overlapped.hEvent = _kernel32.CreateEventW(None, True, False, None)
            return overlapped

        def recv(self, n: int) -> bytes:
            '''Read up to n bytes using overlapped I/O.'''
            buffer = ctypes.create_string_buffer(n)
            bytes_read = wintypes.DWORD()
            overlapped = self._create_overlapped_event()

            try:
                success = _kernel32.ReadFile(
                    self._handle,
                    buffer,
                    n,
                    ctypes.byref(bytes_read),
                    ctypes.byref(overlapped)
                )

                if not success:
                    error = ctypes.get_last_error()
                    if error == self.ERROR_IO_PENDING:
                        # Wait for the operation to complete
                        _kernel32.GetOverlappedResult(
                            self._handle,
                            ctypes.byref(overlapped),
                            ctypes.byref(bytes_read),
                            True  # wait
                        )
                    else:
                        raise OSError(f"ReadFile failed with error {error}")
            finally:
                _kernel32.CloseHandle(overlapped.hEvent)

            return buffer.raw[:bytes_read.value]

        def sendall(self, data: bytes) -> None:
            '''Write all data using overlapped I/O.'''
            bytes_written = wintypes.DWORD()
            overlapped = self._create_overlapped_event()

            try:
                offset = 0
                while offset < len(data):
                    chunk = data[offset:]

                    success = _kernel32.WriteFile(
                        self._handle,
                        chunk,
                        len(chunk),
                        ctypes.byref(bytes_written),
                        ctypes.byref(overlapped)
                    )

                    if not success:
                        error = ctypes.get_last_error()
                        if error == self.ERROR_IO_PENDING:
                            # Wait for the operation to complete
                            _kernel32.GetOverlappedResult(
                                self._handle,
                                ctypes.byref(overlapped),
                                ctypes.byref(bytes_written),
                                True  # wait
                            )
                        else:
                            raise OSError(f"WriteFile failed with error {error}")

                    offset += bytes_written.value
            finally:
                _kernel32.CloseHandle(overlapped.hEvent)

        def close(self) -> None:
            '''Close the handle.'''
            if self._handle is not None and self._handle != self.INVALID_HANDLE_VALUE:
                _kernel32.CloseHandle(self._handle)
                self._handle = None

    def _connect_pipe(socket_path: str, timeout_sec: float) -> _PipeSocket:
        '''Connect to a named pipe with timeout, retrying until available.'''
        pipe_path = f"\\\\.\\pipe\\{socket_path}"
        _logger.debug("Connecting to: %s", pipe_path)

        start_time = time.time()
        while (time.time() - start_time) < timeout_sec:
            try:
                return _PipeSocket(pipe_path)
            except FileNotFoundError:
                time.sleep(0.1)
        raise TimeoutError("Connection timeout")

else:
    # On Unix, use socket.socket directly as the pipe socket type
    _PipeSocket = socket.socket  # type: ignore[misc]

    def _connect_pipe(socket_path: str, timeout_sec: float) -> _PipeSocket:
        '''Connect to a Unix domain socket with timeout.'''
        _logger.debug("Connecting to: %s", socket_path)

        start_time = time.time()
        while (time.time() - start_time) < timeout_sec:
            try:
                sock = _PipeSocket(socket.AF_UNIX, socket.SOCK_STREAM)
                sock.settimeout(timeout_sec)
                sock.connect(socket_path)
                sock.settimeout(None)  # Set to blocking mode
                return sock
            except FileNotFoundError:
                time.sleep(0.1)
        raise TimeoutError("Connection timeout")


# ============================================================================
# Base Types
# ============================================================================

class AtsErrorDetails(typing.TypedDict, total=False):
    '''Error details for ATS errors.'''
    parameter: str
    expected: str
    actual: str


class AtsErrorData(typing.TypedDict, total=False):
    '''Structured error from ATS capability invocation.'''
    code: str
    message: str
    capability: str
    details: AtsErrorDetails


class AtsErrorCodes:
    '''ATS error codes returned by the server.'''
    CAPABILITY_NOT_FOUND = "CAPABILITY_NOT_FOUND"
    HANDLE_NOT_FOUND = "HANDLE_NOT_FOUND"
    TYPE_MISMATCH = "TYPE_MISMATCH"
    INVALID_ARGUMENT = "INVALID_ARGUMENT"
    ARGUMENT_OUT_OF_RANGE = "ARGUMENT_OUT_OF_RANGE"
    CALLBACK_ERROR = "CALLBACK_ERROR"
    INTERNAL_ERROR = "INTERNAL_ERROR"


def _simplify_type_name(typestring: str) -> str:
    '''Simplify a .NET type name for readability in error messages.'''
    # Collapse any embedded newlines/whitespace so patterns can match
    collapsed = " ".join(typestring.split())
    # Check for generic builder pattern like "IResourceBuilder`1[Namespace.Type]"
    idx = collapsed.find(_BUILDER_GENERIC_MARKER)
    if idx >= 0:
        inner = collapsed[idx + len(_BUILDER_GENERIC_MARKER):]
        # Skip any extra opening brackets (e.g. "[[")
        inner = inner.lstrip("[").strip()
        # Take only the qualified name (before any comma/assembly info or closing brackets)
        inner = inner.split(",")[0].split("]")[0].strip()
        # Get simple class name from fully-qualified name
        inner_type = inner.rsplit(".", 1)[-1]
        # Strip leading 'I' from interface names (e.g., IResourceWithConnectionString -> ResourceWithConnectionString)
        if len(inner_type) > 1 and inner_type[0] == "I" and inner_type[1].isupper():
            inner_type = inner_type[1:]
        return inner_type
    return typestring.split(".")[-1]  # Fallback: take simple name from fully-qualified type


def _format_type_error(error: AtsErrorData) -> Exception:
    '''Formats an ATS type error into a Python exception with a helpful message.'''
    try:
        details = error["details"]  # type: ignore[index]
        parameter = details["parameter"]  # type: ignore[index]
        expected = _simplify_type_name(details["expected"])  # type: ignore[index]
        actual = _simplify_type_name(details["actual"])  # type: ignore[index]

        message_prefix = ""
        if capability := error.get("capability"):
            message_prefix = f" in '{capability.split('/')[-1]}'"
        message = f"Type mismatch{message_prefix} for parameter '{parameter}': expected {expected}, got {actual}"

        return TypeError(message)
    except KeyError:
        if message := error.get("message"):
            return TypeError(message)
        if capability := error.get("capability"):
            return TypeError(f"Type mismatch in capability '{capability}'")
        return TypeError("Parameter type mismatch.")


def _is_ats_error(value: typing.Any) -> bool:
    '''Type guard to check if a value is an ATS error response.'''
    return (
        isinstance(value, dict)
        and "$error" in value
        and isinstance(value["$error"], dict)
    )


def _is_marshalled_handle(value: typing.Any) -> bool:
    '''Type guard to check if a value is a marshalled handle.'''
    return (
        isinstance(value, dict)
        and "$handle" in value
        and "$type" in value
    )


# ============================================================================
# Handle
# ============================================================================

class Handle:
    '''
    A typed handle to a .NET object in the ATS system.
    Handles are opaque references that can be passed to capabilities.
    '''

    def __init__(self, handle_data: typing.Mapping[str, typing.Any]) -> None:
        self._handle_id = handle_data["$handle"]
        self._type_id = handle_data["$type"]

    @property
    def handle_id(self) -> str:
        '''The handle ID (instance number)'''
        return self._handle_id

    @property
    def type_id(self) -> str:
        '''The ATS type ID'''
        return self._type_id

    def to_json(self) -> typing.Mapping[str, typing.Any]:
        '''Serialize for JSON-RPC transport'''
        return {
            "$handle": self._handle_id,
            "$type": self._type_id
        }

    def __repr__(self) -> str:
        '''String representation for debugging'''
        friendly_name = self._type_id.split("/")[-1].split(".")[-1]
        return f"Handle[{friendly_name}]({self._handle_id})"


# ============================================================================
# Handle Wrapper Registry
# ============================================================================

_HandleWrapperFactory = typing.Callable[[Handle, "AspireClient"], typing.Any]

# Registry of handle wrapper factories by type ID
_handle_wrapper_registry: dict[str, _HandleWrapperFactory] = {}


def _register_handle_wrapper(type_id: str, factory: _HandleWrapperFactory) -> None:
    '''
    Register a wrapper factory for a type ID.
    Called by generated code to register wrapper classes.
    '''
    _handle_wrapper_registry[type_id] = factory


def _wrap_if_handle(value: typing.Any, client: AspireClient | None = None, kwargs: typing.Mapping[str, typing.Any] | None = None) -> typing.Any:
    '''
    Checks if a value is a marshalled handle and wraps it appropriately.
    Uses the wrapper registry to create typed wrapper instances when available.
    '''
    if isinstance(value, dict) and _is_marshalled_handle(value):
        handle = Handle(value)
        type_id = value["$type"]

        # Try to find a registered wrapper factory for this type
        if type_id and client:
            factory = _handle_wrapper_registry.get(type_id)
            if factory:
                if kwargs:
                    return factory(handle, client, **kwargs)
                return factory(handle, client)

        return handle

    return value


# ============================================================================
# Errors
# ============================================================================

class AspireError(Exception):
    '''Base class for Aspire errors.'''
    error: AtsErrorData

    def __init__(self, error: AtsErrorData) -> None:
        super().__init__(error.get("message", "Unknown error"))
        self.error = error

    @property
    def code(self) -> str:
        '''Machine-readable error code'''
        return self.error.get("code", "UNKNOWN")

    @property
    def capability(self) -> str | None:
        '''The capability that failed (if applicable)'''
        return self.error.get("capability")


class OperationCancelled(Exception):
    '''Error thrown when a cancellation token is invoked.'''
    pass

# ============================================================================
# JSON Encoder
# ============================================================================

@typing.runtime_checkable
class _ReferenceHandle(typing.Protocol):
    '''Protocol for objects that have a handle property.'''

    @property
    def handle(self) -> Handle:
        ...


def _timedelta_as_isostr(td: datetime.timedelta) -> str:
    '''Converts a datetime.timedelta object into an ISO 8601 formatted string, e.g. 'P4DT12H30M05S'

    Function adapted from the Tin Can Python project: https://github.com/RusticiSoftware/TinCanPython

    :param td: The timedelta object to convert
    :type td: datetime.timedelta
    :return: An ISO 8601 formatted string representing the timedelta object
    :rtype: str
    '''

    # Split seconds to larger units
    seconds = td.total_seconds()
    minutes, seconds = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)
    days, hours = divmod(hours, 24)

    days, hours, minutes = list(map(int, (days, hours, minutes)))
    seconds = round(seconds, 6)

    # Build date
    date_str = ""
    if days:
        date_str = "%sD" % days

    # Build time
    time_str = "T"

    # Hours
    bigger_exists = date_str or hours
    if bigger_exists:
        time_str += "{:02}H".format(hours)

    # Minutes
    bigger_exists = bigger_exists or minutes
    if bigger_exists:
        time_str += "{:02}M".format(minutes)

    # Seconds
    try:
        if seconds.is_integer():
            seconds_string = "{:02}".format(int(seconds))
        else:
            # 9 chars long w/ leading 0, 6 digits after decimal
            seconds_string = "%09.6f" % seconds
            # Remove trailing zeros
            seconds_string = seconds_string.rstrip("0")
    except AttributeError:  # int.is_integer() raises
        seconds_string = "{:02}".format(seconds)

    time_str += "{}S".format(seconds_string)
    return "P" + date_str + time_str


def _datetime_as_isostr(dt: typing.Union[datetime.datetime, datetime.date, datetime.time, datetime.timedelta]) -> str:
    '''Converts a datetime.(datetime|date|time|timedelta) object into an ISO 8601 formatted string.

    :param dt: The datetime object to convert
    :type dt: datetime.datetime or datetime.date or datetime.time or datetime.timedelta
    :return: An ISO 8601 formatted string representing the datetime object
    :rtype: str
    '''
    # First try datetime.datetime
    if hasattr(dt, "year") and hasattr(dt, "hour"):
        dt = typing.cast(datetime.datetime, dt)
        # astimezone() fails for naive times in Python 2.7, so make make sure dt is aware (tzinfo is set)
        if not dt.tzinfo:
            iso_formatted = dt.replace(tzinfo=datetime.timezone.utc).isoformat()
        else:
            iso_formatted = dt.astimezone(datetime.timezone.utc).isoformat()
        # Replace the trailing "+00:00" UTC offset with "Z" (RFC 3339: https://www.ietf.org/rfc/rfc3339.txt)
        return iso_formatted.replace("+00:00", "Z")
    # Next try datetime.date or datetime.time
    try:
        dt = typing.cast(typing.Union[datetime.date, datetime.time], dt)
        return dt.isoformat()
    # Last, try datetime.timedelta
    except AttributeError:
        dt = typing.cast(datetime.timedelta, dt)
        return _timedelta_as_isostr(dt)


class _AspireJSONEncoder(json.JSONEncoder):
    '''A JSON encoder that's capable of serializing datetime objects and bytes.'''

    def default(self, o: typing.Any) -> typing.Any:
        '''Override the default method to handle datetime and bytes serialization.
        :param o: The object to serialize.
        :type o: Any
        :return: A JSON-serializable representation of the object.
        :rtype: Any
        '''
        if isinstance(o, ReferenceExpression):
            return o.to_json()
        if isinstance(o, _ReferenceHandle):
            return o.handle.to_json()
        if isinstance(o, Handle):
            return o.to_json()
        if isinstance(o, (bytes, bytearray)):
            return base64.b64encode(o).decode()
        try:
            return _datetime_as_isostr(o)
        except AttributeError:
            pass
        return super().default(o)


class AspireClient:
    '''Client for connecting to the Aspire AppHost via socket/named pipe (synchronous with threads).'''

    # Default heartbeat interval in seconds
    DEFAULT_HEARTBEAT_INTERVAL = 5.0

    def __init__(self, socket_path: str, *, debug: bool | None = None, heartbeat_interval: float | None = None) -> None:
        self.socket_path = socket_path
        self.debug = debug if debug is not None else False
        self._socket: _PipeSocket | None = None
        self._request_id = 0
        self._pending_requests: dict[int, threading.Event] = {}
        self._pending_results: dict[int, tuple[typing.Any, Exception | None]] = {}
        self._disconnect_callbacks: list[typing.Callable[[], None]] = []
        self._receive_thread: threading.Thread | None = None
        self._heartbeat_thread: threading.Thread | None = None
        self._cancellation_threads: dict[str, tuple[typing.Callable[[], None], threading.Thread]] = {}
        self._cancellation_id = 0
        self._heartbeat_interval = heartbeat_interval if heartbeat_interval is not None else self.DEFAULT_HEARTBEAT_INTERVAL
        self._heartbeat_stop_event = threading.Event()
        self._connected = False
        self._connection_error: ConnectionError | None = None
        self._lock = threading.Lock()
        self._callback_registry: dict[str, typing.Callable[..., typing.Any]] = {}
        self._callback_id_counter = 0
        self._callback_threads: dict[str, threading.Thread] = {}

    def on_disconnect(self, callback: typing.Callable[[], None]) -> None:
        '''Register a callback to be called when the connection is lost'''
        with self._lock:
            self._disconnect_callbacks.append(callback)

    def _handle_sigint(self, signum: int, frame: typing.Any) -> None:
        '''Handle SIGINT (Ctrl+C) by cancelling all pending requests and closing connection.'''
        # Close the connection
        self._close_connection(ConnectionError("Shutdown by user"))
        # TODO: This will just shutdown the process, which may be best as this will likely
        # be run with aspire.run, which will prefer a quick exit with zero exit code.
        # Not entirely sure if this signal will be propagated correctly by .NET.... well see.
        sys.exit(0)

    def _close_connection(self, error: ConnectionError | None = None) -> None:
        '''
        Central method for closing the connection. Thread-safe and idempotent.

        Args:
            error: If provided, this is an unexpected disconnection and callbacks
                will be notified. If None, this is an intentional disconnect.

        This method:
        - Closes the socket
        - Sets _connected = False
        - Stores the error (if provided and not already set)
        - Signals the heartbeat thread to stop
        - Notifies disconnect callbacks (only on first unexpected disconnection)
        '''
        should_notify = False
        callbacks: list[typing.Callable[[], None]] = []

        with self._lock:
            # Close socket if open
            if self._socket is not None:
                try:
                    self._socket.close()
                except Exception:
                    pass
                self._socket = None

            # Mark as disconnected
            should_notify = self._connected
            self._connected = False
            callbacks = list(self._disconnect_callbacks)
            if should_notify:
                if error:
                    _logger.error("Closing connection to AppHost with error: %s", error)
                else:
                    _logger.info("Closing connection to AppHost")

            # Handle error state
            if error is not None:
                # Unexpected disconnection - store error if first failure
                if self._connection_error is None:
                    self._connection_error = error
            else:
                # Intentional disconnect - clear any previous error?
                self._connection_error = None

            for event in self._pending_requests.values():
                event.set()
            for (cancellation, _ ) in self._cancellation_threads.values():
                # Threads will exit on their own when they notice disconnection
                cancellation()

        # Signal heartbeat to stop (outside lock, it's thread-safe)
        self._heartbeat_stop_event.set()

        # Notify callbacks outside lock to avoid deadlocks
        if should_notify:
            for callback in callbacks:
                try:
                    callback()
                except Exception:
                    pass

    def connect(self, timeout_ms: int = 10000) -> None:
        '''Connect to the Aspire AppHost'''
        timeout_sec = timeout_ms / 1000.0
        socket = _connect_pipe(self.socket_path, timeout_sec)

        with self._lock:
            self._socket = socket
            self._connected = True
            self._connection_error = None

        self._heartbeat_stop_event.clear()

        # Install SIGINT handler for clean Ctrl+C handling
        signal.signal(signal.SIGINT, self._handle_sigint)

        _logger.info("Connected to AppHost")

        # Start receiving messages in a background thread
        self._receive_thread = threading.Thread(target=self._receive_loop, daemon=True)
        self._receive_thread.start()

        # Start heartbeat thread to monitor connection health
        self._heartbeat_thread = threading.Thread(target=self._heartbeat_loop, daemon=True)
        self._heartbeat_thread.start()

    def _recv_exactly(self, n: int) -> bytes:
        '''Read exactly n bytes from the socket'''
        data = b""
        while len(data) < n:
            chunk = typing.cast(_PipeSocket, self._socket).recv(n - len(data))
            if not chunk:
                raise ConnectionError("Connection closed")
            data += chunk
        return data

    def _read_line(self) -> bytes:
        '''Read a line ending with \\r\\n from the socket'''
        line = b""
        while True:
            byte = typing.cast(_PipeSocket, self._socket).recv(1)
            if not byte:
                raise ConnectionError("Connection closed")
            line += byte
            if line.endswith(b"\r\n"):
                return line[:-2]  # Remove \r\n

    def _read_headers(self) -> dict[str, str]:
        '''Read HTTP-style headers until empty line.

        Enforces limits on header count and total size to prevent
        memory exhaustion from a malicious peer.
        '''
        headers: dict[str, str] = {}
        total_bytes = 0
        while True:
            line = self._read_line()
            if not line:  # Empty line signals end of headers
                break

            total_bytes += len(line) + 2  # account for \r\n
            if len(headers) >= _MAX_HEADER_COUNT:
                raise ConnectionError(f"Too many headers (limit {_MAX_HEADER_COUNT})")
            if total_bytes > _MAX_HEADER_BYTES:
                raise ConnectionError(f"Headers too large (limit {_MAX_HEADER_BYTES} bytes)")

            # Parse "Header-Name: value"
            if b":" in line:
                name, value = line.split(b":", 1)
                headers[name.decode("utf-8").strip().lower()] = value.decode("utf-8").strip()
        return headers

    def _receive_loop(self) -> None:
        '''Receive and process messages from the server'''
        try:
            while self._connected and self._socket:
                # Read HTTP-style headers (HeaderDelimitedMessageHandler format)
                headers = self._read_headers()
                content_length = int(headers.get("content-length", "0"))

                if content_length == 0:
                    continue

                if content_length > _MAX_MESSAGE_SIZE:
                    raise ConnectionError(
                        f"Message too large: {content_length} bytes "
                        f"(limit {_MAX_MESSAGE_SIZE} bytes)"
                    )

                # Read message content
                message_bytes = self._recv_exactly(content_length)
                message_str = message_bytes.decode("utf-8")
                message = json.loads(message_str)
                if self.debug:
                    if message.get("result") == "pong":
                        _logger.debug("<- %s", message)
                    else:
                        _logger.info("<- %s", message)

                # Handle response or request
                if "method" in message:
                    # This is a request from the server (callback invocation)
                    self._handle_server_request(message)
                elif "id" in message:
                    # This is a response to our request
                    request_id = message["id"]
                    with self._lock:
                        if request_id in self._pending_requests:
                            event = self._pending_requests[request_id]
                            if "error" in message:
                                self._pending_results[request_id] = (None, Exception(message["error"]["message"]))
                            else:
                                self._pending_results[request_id] = (message.get("result"), None)
                            event.set()
        except AttributeError:
            # This probably means the socket was closed
            pass
        except ConnectionError as e:
            self._close_connection(e)
        except Exception as e:
            self._close_connection(ConnectionError(f"Receive loop error: {e}"))

    def _heartbeat_loop(self) -> None:
        '''Periodically ping the server to check connection health.'''
        while not self._heartbeat_stop_event.wait(timeout=self._heartbeat_interval):
            with self._lock:
                if not self._connected:
                    break
            try:
                self.ping()
            except Exception as e:
                self._close_connection(ConnectionError(f"Heartbeat failed: {e}"))
                break

    def _handle_server_request(self, message: dict[str, typing.Any]) -> None:
        '''Handle a request from the server (e.g., callback invocation)'''
        method = message.get("method")
        request_id = message.get("id")
        params = message.get("params", [])

        if method == "invokeCallback":
            _logger.debug("Invoking callback with params: %s", params)
            callback_id = str(params[0]) if len(params) > 0 else None
            args = params[1] if len(params) > 1 else None

            # Spawn a separate thread to handle the callback so receive_loop isn't blocked
            thread_id = f"cb_thread_{request_id}_{callback_id}"
            thread = threading.Thread(
                target=self._execute_callback_thread,
                args=(callback_id, args, request_id, thread_id),
                daemon=True
            )
            with self._lock:
                self._callback_threads[thread_id] = thread
            thread.start()

    def _execute_callback_thread(
        self,
        callback_id: str | None,
        args: typing.Any,
        request_id: int | None,
        thread_id: str
    ) -> None:
        '''Execute a callback in a separate thread and send the response.'''
        result = None
        error = None
        try:
            try:
                with self._lock:
                    callback = self._callback_registry.get(callback_id) if callback_id else None

                if callback:
                    result = callback(args, self)
                    _logger.debug("Callback result: %s", result)
                else:
                    error = {"code": -32601, "message": f"Callback not found: {callback_id}"}
            except OperationCancelled as e:
                _logger.debug("Callback cancelled: %s", e)
                return

            except Exception as e:
                _logger.warning("Exception in callback: %s", e)
                # Include type and origin (file:line) for diagnosability,
                # but omit the full stack trace and error message to avoid
                # leaking sensitive internal details to the server.
                tb = e.__traceback__
                if tb is not None:
                    # Walk to the innermost frame (actual error site)
                    while tb.tb_next is not None:
                        tb = tb.tb_next
                    filename = tb.tb_frame.f_code.co_filename
                    # Send only the basename to avoid leaking full filesystem paths
                    basename = filename.rsplit("/", 1)[-1].rsplit("\\", 1)[-1]
                    lineno = tb.tb_lineno
                    location = f" at {basename}:{lineno}"
                else:
                    location = ""
                error = {"code": -32603, "message": f"Internal callback error: {type(e).__name__}{location}"}

            # Send response
            if request_id is not None:
                if error:
                    response = {
                        "jsonrpc": "2.0",
                        "id": request_id,
                        "error": error
                    }
                else:
                    response = {
                        "jsonrpc": "2.0",
                        "id": request_id,
                        "result": result
                    }
                try:
                    self._send_message(response)
                except Exception as e:
                    _logger.error("Failed to send callback response: %s", e)
        finally:
            # Clean up thread reference
            with self._lock:
                self._callback_threads.pop(thread_id, None)

    def _send_message(self, message: dict[str, typing.Any]) -> None:
        '''Send a JSON-RPC message to the server using header-delimited format'''
        if self.debug:
            if message.get("method") == "ping":
                _logger.debug("-> %s", message)
            else:
                _logger.info("-> %s", message)
        message_str = json.dumps(message, cls=_AspireJSONEncoder)
        message_bytes = message_str.encode("utf-8")
        content_length = len(message_bytes)

        # Send with HTTP-style headers (HeaderDelimitedMessageHandler format)
        header = f"Content-Length: {content_length}\r\n\r\n"
        header_bytes = header.encode("utf-8")
        with self._lock:
            typing.cast(_PipeSocket, self._socket).sendall(header_bytes + message_bytes)

    def _check_connection(self) -> None:
        '''Check if connected and raise stored connection error if present.'''
        with self._lock:
            if self._connection_error:
                raise self._connection_error
            if not self._connected:
                raise RuntimeError("Not connected to AppHost")

    def ping(self) -> str:
        '''Ping the server'''
        self._check_connection()
        return self._send_request("ping")

    def authenticate(self, token: str) -> None:
        '''Authenticate to the AppHost server with a session token.'''
        if not bool(self._send_request("authenticate", token)):
            raise RuntimeError("Failed to authenticate to the AppHost server.")

    def invoke_capability(
        self,
        capability_id: str,
        args: dict[str, typing.Any] | None = None,
        kwargs: typing.Mapping[str, typing.Any] | None = None
    ) -> typing.Any:
        '''
        Invoke an ATS capability by ID.

        Capabilities are operations exposed by [AspireExport] attributes.
        Results are automatically wrapped in Handle objects when applicable.
        '''
        self._check_connection()
        result = self._send_request("invokeCapability", capability_id, args or {})

        # Check for structured error response
        if _is_ats_error(result):
            if result["$error"].get("code") == AtsErrorCodes.TYPE_MISMATCH:
                raise _format_type_error(result["$error"])
            raise AspireError(result["$error"])

        # Wrap handles automatically
        return _wrap_if_handle(result, self, kwargs)

    def _send_request(self, method: str, *params: typing.Any) -> typing.Any:
        '''Send a JSON-RPC request and wait for response'''
        with self._lock:
            self._request_id += 1
            request_id = self._request_id

        request = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": list(params) if params else []
        }
        # Create event for response
        event = threading.Event()
        with self._lock:
            self._pending_requests[request_id] = event

        # Send request
        self._send_message(request)

        # Wait for response
        event.wait()

        # Get result
        with self._lock:
            if request_id not in self._pending_results:
                # Request was cancelled/interrupted
                if self._connection_error:
                    raise self._connection_error
                raise RuntimeError("Request was cancelled")
            del self._pending_requests[request_id]
            result, error = self._pending_results.pop(request_id)

        if error:
            raise error

        return result

    def register_cancellation_token(self, cancellation_timeout: int | None) -> str | None:
        if not cancellation_timeout:
            return None

        with self._lock:
            cancellation_id = f"ct_{self._cancellation_id}_{int(time.time() * 1000)}"
            cancellation_token = threading.Event()

            def cancellation_thread():
                cancellation_token.wait()
                self._check_connection()
                # Send cancellation request to server
                try:
                    self._send_request("cancelToken", cancellation_id)
                except Exception:
                    pass  # Ignore errors during cancellation
                self._cancellation_threads.pop(cancellation_id, None)

            thread = threading.Thread(target=cancellation_thread, daemon=True)
            cancel_timer = threading.Timer(cancellation_timeout, cancellation_token.set)

            def shutdown():
                cancel_timer.cancel()
                cancellation_token.set()

            self._cancellation_threads[cancellation_id] = (shutdown, thread)
            self._cancellation_id += 1
            thread.start()
            cancel_timer.start()

        return cancellation_id

    def disconnect(self) -> None:
        '''Disconnect from the server'''
        self._close_connection(error=None)  # Intentional disconnect, no error

        if self._heartbeat_thread and self._heartbeat_thread.is_alive():
            self._heartbeat_thread.join(timeout=1.0)

        if self._receive_thread and self._receive_thread.is_alive():
            self._receive_thread.join(timeout=1.0)

        # Wait for any pending callback threads to finish
        with self._lock:
            callback_threads = list(self._callback_threads.values())
        for thread in callback_threads:
            if thread.is_alive():
                thread.join(timeout=1.0)
        for (_, thread) in self._cancellation_threads.values():
            if thread.is_alive():
                thread.join(timeout=1.0)

    def register_callback(self, callback: typing.Callable[..., typing.Any] | None) -> str | None:
        '''
        Register a callback function that can be invoked from the .NET side.
        Returns a callback ID that should be passed to methods accepting callbacks.

        .NET passes arguments as an object with positional keys: { p0: value0, p1: value1, ... }
        This function automatically extracts positional parameters and wraps handles.
        '''
        if callback is None:
            return None

        with self._lock:
            self._callback_id_counter += 1
            callback_id = f"callback_{secrets.token_hex(16)}"

        def wrapper(args: typing.Any, client: AspireClient) -> typing.Any:
            # .NET sends args as object { p0: value0, p1: value1, ... }
            if isinstance(args, dict):
                arg_array = []
                i = 0
                while True:
                    key = f"p{i}"
                    if key in args:
                        arg_array.append(_wrap_if_handle(args[key], client))
                        i += 1
                    else:
                        break

                if arg_array:
                    return callback(*arg_array)

            # No args or null
            if args is None:
                return callback()

            # Single primitive value (shouldn't happen with current protocol)
            return callback(_wrap_if_handle(args, client))

        with self._lock:
            self._callback_registry[callback_id] = wrapper
        return callback_id

    @property
    def connected(self) -> bool:
        '''Check if connected to the server'''
        return self._connected


# ============================================================================
# CancellationToken
# ============================================================================

class CancellationToken:
    '''Represents a cancellation token that can be used to cancel a callback in progress.'''

    handle: Handle

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self.handle = handle
        self._client = client
    
    def cancel(self) -> None:
        '''Cancel the token, which will signal the server to cancel the associated operation.'''
        self._client._send_request("cancelToken", self.handle.handle_id)
        raise OperationCancelled(self.handle.handle_id)


# ============================================================================
# Reference Expression
# ============================================================================

class ReferenceExpression:
    '''Represents a reference expression passed to capabilities.
    Supports both value mode (format + valueProviders) and conditional mode (condition + whenTrue + whenFalse).
    '''

    def __init__(self, handle: Handle | None, **kwargs) -> None:
        '''
        Creates a reference expression from a format string and value providers.

        Args:
            format_str: Format string with {0}, {1}, etc. placeholders
            *value_providers: Handles to value providers

        Returns:
            A ReferenceExpression instance
        '''
        self._handle = handle
        self._format = kwargs.get("format_str")
        self._value_providers = kwargs.get("value_providers", [])
        self._condition = kwargs.get("condition")
        self._when_true = kwargs.get("when_true")
        self._when_false = kwargs.get("when_false")
        self._match_value = kwargs.get("match")
    
    @classmethod
    def format_string(cls, format_str: str, *value_providers: typing.Any) -> "ReferenceExpression":
        '''
        Creates a reference expression from a format string and value providers.

        Args:
            format_str: Format string with {0}, {1}, etc. placeholders
            *value_providers: Handles to value providers

        Returns:
            A ReferenceExpression instance
        '''
        providers = [_extract_handle_for_expr(v) for v in value_providers]
        return cls(None, format_str=format_str, value_providers=providers)

    @classmethod
    def conditional(cls, condition: typing.Any, **kwargs) -> "ReferenceExpression":
        '''
        Creates a conditional reference expression.

        Args:
            condition: The condition to evaluate
            match: The value to match against
            when_true: ReferenceExpression if condition matches
            when_false: ReferenceExpression if condition does not match

        Returns:
            A ReferenceExpression instance
        '''
        return cls(None, condition=condition, **kwargs)

    def to_json(self) -> typing.Mapping[str, typing.Any]:
        '''
        Serializes the reference expression for JSON-RPC transport.
        Uses the $expr format recognized by the server.
        '''
        if self._handle:
            return self._handle.to_json()
        if self._condition and self._when_true and self._when_false and self._match_value is not None:
            return {
                "$expr": {
                    "condition": self._condition,
                    "whenTrue": self._when_true.to_json(),
                    "whenFalse": self._when_false.to_json(),
                    "matchValue": self._match_value,
                }
            }
        if self._format:
            result: dict[str, typing.Any] = {
                "$expr": {
                    "format": self._format,
                }
            }
            if self._value_providers:
                result["$expr"]["valueProviders"] = self._value_providers
            return result
        raise ValueError("Invalid ReferenceExpression: must have either handle, condition, or format")

    def __repr__(self) -> str:
        if self._handle:
            return f"ReferenceExpression(handle={self._handle.handle_id})"
        if self._condition:
            return "ReferenceExpression(conditional)"
        return f"ReferenceExpression(formattedString)"


def _extract_handle_for_expr(value: typing.Any) -> typing.Any:
    '''
    Extracts a value for use in reference expressions.
    Supports handles (objects) and string literals.
    '''
    if value is None:
        raise ValueError("Cannot use None in reference expression")

    # String literals - include directly in the expression
    if isinstance(value, str):
        return value

    # Number literals - convert to string
    if isinstance(value, (int, float)):
        return str(value)

    # Handle objects - get their JSON representation
    if isinstance(value, (Handle, _ReferenceHandle)):
        return value

    # Objects with $handle property (already in handle format)
    if isinstance(value, dict) and "$handle" in value:
        return value

    raise ValueError(
        f"Cannot use value of type {type(value).__name__} in reference expression. "
        f"Expected a handle object, string, or number."
    )


def string_expr(value: str, **kwargs: typing.Any) -> ReferenceExpression:
    '''
    Helper function for creating reference expressions with named placeholders.

    Use this to create dynamic expressions that reference endpoints, parameters, and other
    value providers. The expression is evaluated at runtime by Aspire.

    Example:
        ```python
        redis = await builder.add_redis("cache")
        endpoint = await redis.get_endpoint("tcp")

        # Create a reference expression using named placeholders
        expr = string_expr("redis://{host}:{port}", host=endpoint, port=6379)

        # Use it in an environment variable
        await api.with_environment("REDIS_URL", expr)
        ```
    '''
    # Replace named placeholders with numbered ones
    value_providers = []
    format_str = value

    for key, value in kwargs.items():
        placeholder = f"{{{key}}}"
        if placeholder in format_str:
            index = len(value_providers)
            format_str = format_str.replace(placeholder, f"{{{index}}}")
            value_providers.append(value)

    return ReferenceExpression.format_string(format_str, *value_providers)


def conditional_expr(condition: typing.Any, *, match: str, when_true: str | ReferenceExpression, when_false: str | ReferenceExpression) -> ReferenceExpression:
    '''
    Helper function for creating conditional reference expressions.

    This allows you to create dynamic expressions that evaluate conditions at runtime.

    Example:
        ```python
        # Create a conditional expression based on an environment variable
        condition = {"$handle": "Aspire.Hosting.EnvironmentVariableReference:ENVIRONMENT"}
        expr = conditional_expr(condition, match="production",
            when_true="redis://prod-redis:6379",
            when_false="redis://dev-redis:6379"
        )

        await api.with_environment("REDIS_URL", expr)
        ```
    '''
    if isinstance(when_true, str):
        when_true = ReferenceExpression.format_string(when_true)
    if isinstance(when_false, str):
        when_false = ReferenceExpression.format_string(when_false)
    return ReferenceExpression.conditional(condition, match=match, when_true=when_true, when_false=when_false)


# ============================================================================
# AspireList[T] - Mutable List Wrapper
# ============================================================================

TItem = typing.TypeVar("TItem")


class AspireList(typing.MutableSequence[TItem]):
    '''
    Wrapper for a mutable .NET List<T>.

    Example:
        ```python
        items = await resource.get_items()  # Returns AspireList[ItemBuilder]

        # Modify the list
        items.append(new_item)
        items[0] = another_item
        del items[1]
        ```
    '''

    def __init__(
        self,
        handle: Handle,
        client: AspireClient,
    ) -> None:
        self._handle = handle
        self._client = client

    # ---- Required abstract methods from MutableSequence ----

    def __len__(self) -> int:
        '''Gets the number of elements in the list.'''
        result = self._client.invoke_capability(
            "Aspire.Hosting/List.length",
            {"list": self._handle}
        )
        return int(result)

    @typing.overload
    def __getitem__(self, index: int) -> TItem: ...
    @typing.overload
    def __getitem__(self, index: slice) -> list[TItem]: ...
    def __getitem__(self, index: int | slice) -> TItem | list[TItem]:
        '''Gets the element at the specified index or a slice of elements.'''
        if not isinstance(index, int):
            raise NotImplementedError("Get by slice not yet supported.")
        result = self._client.invoke_capability(
            "Aspire.Hosting/List.get",
            {"list": self._handle, "index": index}
        )
        return list(result)

    @typing.overload
    def __setitem__(self, index: int, value: TItem) -> None: ...
    @typing.overload
    def __setitem__(self, index: slice, value: typing.Iterable[TItem]) -> None: ...
    def __setitem__(self, index: int | slice, value: TItem | typing.Iterable[TItem]) -> None:
        '''Sets the element at the specified index or replaces a slice of elements.'''
        if not isinstance(index, int):
            raise NotImplementedError("Set by slice not yet supported.")
        self._client.invoke_capability(
            "Aspire.Hosting/List.set",
            {"list": self._handle, "index": index, "value": value}
        )

    @typing.overload
    def __delitem__(self, index: int) -> None: ...
    @typing.overload
    def __delitem__(self, index: slice) -> None: ...
    def __delitem__(self, index: int | slice) -> None:
        '''Deletes the element at the specified index or a slice of elements.'''
        if not isinstance(index, int):
            raise NotImplementedError("Delete by slice not yet supported.")
        self._client.invoke_capability(
            "Aspire.Hosting/List.removeAt",
            {"list": self._handle, "index": index}
        )

    def insert(self, index: int, value: TItem) -> None:
        '''Inserts an element at the specified index.'''
        self._client.invoke_capability(
            "Aspire.Hosting/List.set",
            {"list": self._handle, "index": index, "item": value}
        )

    def __repr__(self) -> str:
        '''Returns a string representation of the list.'''
        return f"AspireList(handle={self._handle.handle_id})"


# ============================================================================
# AspireDict[K, V] - Mutable Dictionary Wrapper
# ============================================================================

TKey = typing.TypeVar("TKey")
TValue = typing.TypeVar("TValue")


class AspireDict(typing.MutableMapping[TKey, TValue]):
    '''
    Wrapper for a mutable .NET Dictionary<K, V>.

    Example:
        ```python
        config = await resource.get_config()  # Returns AspireDict[str, str]

        # Modify the dict
        config["key"] = "value"
        del config["old_key"]
        ```
    '''

    def __init__(
        self,
        handle: Handle,
        client: AspireClient,
    ) -> None:
        self._handle = handle
        self._client = client

    def __len__(self) -> int:
        '''Gets the number of key-value pairs in the dictionary.'''
        result = self._client.invoke_capability(
            "Aspire.Hosting/Dict.count",
            {"dict": self._handle}
        )
        return int(result)

    def __getitem__(self, key: TKey) -> TValue:
        '''Gets the value associated with the specified key.'''
        result = self._client.invoke_capability(
            "Aspire.Hosting/Dict.get",
            {"dict": self._handle, "key": key}
        )
        if result is None:
            raise KeyError(key)
        return result

    def __setitem__(self, key: TKey, value: TValue) -> None:
        '''Sets the value for the specified key.'''
        self._client.invoke_capability(
            "Aspire.Hosting/Dict.set",
            {"dict": self._handle, "key": key, "value": value}
        )

    def __delitem__(self, key: TKey) -> None:
        '''Removes the key-value pair with the specified key.'''
        result = self._client.invoke_capability(
            "Aspire.Hosting/Dict.remove",
            {"dict": self._handle, "key": key}
        )
        if result is False:
            raise KeyError(key)

    def __iter__(self) -> typing.Iterator[TKey]:
        '''Returns an iterator over the keys.'''
        result = self._client.invoke_capability(
            "Aspire.Hosting/Dict.keys",
            {"dict": self._handle}
        )
        return iter(result)

    def __contains__(self, key: object) -> bool:
        result = self._client.invoke_capability(
            "Aspire.Hosting/Dict.has",
            {"dict": self._handle, "key": key}
        )
        return result

    def __repr__(self) -> str:
        '''Returns a string representation of the dictionary.'''
        return f"AspireDict(handle={self._handle.handle_id})"

    def clear(self) -> None:
        self._client.invoke_capability(
            "Aspire.Hosting/Dict.clear",
            {"dict": self._handle}
        )


def _validate_type(arg: typing.Any, expected_type: typing.Any) -> bool:
    if typing.get_origin(expected_type) is typing.Iterable:
        if isinstance(arg, str):
            return False
        item_type = typing.get_args(expected_type)[0]
        if not isinstance(arg, typing.Iterable):
            return False
        for item in arg:
            if not _validate_type(item, item_type):
                return False
    elif typing.get_origin(expected_type) is typing.Mapping:
        key_type, value_type = typing.get_args(expected_type)
        if not isinstance(arg, typing.Mapping):
            return False
        for key, value in arg.items():
            if not _validate_type(key, key_type):
                return False
            if not _validate_type(value, value_type):
                return False
    elif typing.get_origin(expected_type) is typing.Callable:
        return callable(arg)
    elif isinstance(arg, (tuple, typing.Mapping)):
        return False
    elif typing.get_origin(expected_type) is typing.Literal:
        if arg not in typing.get_args(expected_type):
            return False
    elif expected_type is None:
        if arg is not None:
            return False
    elif subtypes := typing.get_args(expected_type):
        # This is probably a Union type
        return any([_validate_type(arg, subtype) for subtype in subtypes])
    elif not isinstance(arg, expected_type):
        return False
    return True


def _validate_tuple_types(args: typing.Any, arg_types: tuple[typing.Any, ...]) -> bool:
    if not isinstance(args, tuple):
        return False
    if len(args) != len(arg_types):
        return False
    for arg, expected_type in zip(args, arg_types):
        if not _validate_type(arg, expected_type):
            return False
    return True


def _validate_dict_types(args: typing.Any, arg_types: typing.Any) -> bool:
    if not isinstance(args, typing.Mapping):
        return False
    type_hints = typing.get_type_hints(arg_types, include_extras=True)
    for key, expected_type in type_hints.items():
        if typing.get_origin(expected_type) is typing.Required:
            expected_type = typing.get_args(expected_type)[0]
            if key not in args:
                return False
        if key not in args:
            continue
        value = args[key]
        if not _validate_type(value, expected_type):
            return False
    return True


# ============================================================================
# Enum Types
# ============================================================================

TestPersistenceMode = typing.Literal["None", "Volume", "Bind"]

TestResourceStatus = typing.Literal["Pending", "Running", "Stopped", "Failed"]


# ============================================================================
# Method Parameters
# ============================================================================


class OptionalStringParameters(typing.TypedDict, total=False):
    value: str
    enabled: bool


class MergeEndpointParameters(typing.TypedDict, total=False):
    endpoint_name: typing.Required[str]
    port: typing.Required[int]
    scheme: str


class MergeLoggingParameters(typing.TypedDict, total=False):
    log_level: typing.Required[str]
    log_path: str
    enable_console: bool
    max_files: int


class MergeRouteParameters(typing.TypedDict, total=False):
    path: typing.Required[str]
    method: typing.Required[str]
    handler: typing.Required[str]
    priority: typing.Required[int]
    middleware: str


class DataVolumeParameters(typing.TypedDict, total=False):
    name: str
    is_read_only: bool

# ============================================================================
# DTO Classes (Data Transfer Objects)
# ============================================================================

class TestConfigDto(typing.TypedDict, total=False):
    Name: str
    Port: int
    Enabled: bool
    OptionalField: str

class TestDeeplyNestedDto(typing.TypedDict, total=False):
    NestedData: AspireDict[str, AspireList[TestConfigDto]]
    MetadataArray: typing.Iterable[AspireDict[str, str]]

class TestNestedDto(typing.TypedDict, total=False):
    Id: str
    Config: TestConfigDto
    Tags: AspireList[str]
    Counts: AspireDict[str, int]


# ============================================================================
# Exported Values
# ============================================================================

TestConfigs = types.SimpleNamespace()
TestConfigs.Default = typing.cast(TestConfigDto, { "Name": "default", "Port": 6379, "Enabled": True, "OptionalField": "cache" })
TestConfigs.Profiles = types.SimpleNamespace()
TestConfigs.Profiles.Development = typing.cast(TestConfigDto, { "Name": "development", "Port": 5001, "Enabled": False, "OptionalField": None })
TestConfigs.Secure = typing.cast(TestConfigDto, { "Name": "secure", "Port": 6380, "Enabled": True, "OptionalField": None })
TestConfigs.UnicodeGreeting = "你好こんにちは"


# ============================================================================
# Type Classes
# ============================================================================

class DistributedApplicationBuilder:
    '''Type class for DistributedApplicationBuilder.'''

    def __init__(self, client: AspireClient, options: CreateBuilderOptions) -> None:
        self._handle = None
        self._client = client
        self._options = options

    @property
    def handle(self) -> Handle:
        '''Gets the underlying handle for the builder.'''
        if not self._handle:
            raise RuntimeError("Builder connection not initialized.")
        return self._handle

    def __enter__(self) -> DistributedApplicationBuilder:
        self._handle = self._client.invoke_capability(
            'Aspire.Hosting/createBuilder',
            {'argsOrOptions': self._options}
        )
        return self

    def __exit__(self, exc_type, exc_value, traceback) -> None:
        self._client.disconnect()

    def run(self, *, timeout: int | None = None) -> None:
        '''Builds and runs the distributed application.'''
        app = self.build()
        app.run(timeout=timeout)

    def add_test_redis(self, name: str, *, port: int | None = None, **kwargs: typing.Unpack["TestRedisResourceKwargs"]) -> TestRedisResource:  # type: ignore
        """Adds a test Redis resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        if port is not None:
            rpc_args['port'] = port
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/addTestRedis',
            rpc_args,
            kwargs,
        )
        return typing.cast(TestRedisResource, result)

    def add_test_vault(self, name: str, **kwargs: typing.Unpack["TestVaultResourceKwargs"]) -> TestVaultResource:  # type: ignore
        """Adds a test vault resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/addTestVault',
            rpc_args,
            kwargs,
        )
        return typing.cast(TestVaultResource, result)


class AbstractValueProvider(abc.ABC):
    """Abstract base class for AbstractValueProvider."""

class AbstractManifestExpressionProvider(abc.ABC):
    """Abstract base class for AbstractManifestExpressionProvider."""

class AbstractExpressionValue(abc.ABC):
    """Abstract base class for AbstractExpressionValue."""

class AbstractValueWithReferences(abc.ABC):
    """Abstract base class for AbstractValueWithReferences."""

class TestCallbackContext:
    """Type class for TestCallbackContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"TestCallbackContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def name(self) -> str:
        """Gets the Name property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @name.setter
    def name(self, value: str) -> None:
        """Sets the Name property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def value(self) -> int:
        """Gets the Value property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value',
            {'context': self._handle}
        )
        return typing.cast(int, result)

    @value.setter
    def value(self, value: int) -> None:
        """Sets the Value property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue',
            {'context': self._handle, 'value': value}
        )

    def cancel(self) -> None:
        """Cancel the operation."""
        token: CancellationToken = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.cancellationToken',
            {'context': self._handle}
        )
        token.cancel()


class TestCollectionContext:
    """Type class for TestCollectionContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"TestCollectionContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def items(self) -> AspireList[str]:
        """Gets the Items property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items',
            {'context': self._handle}
        )
        return typing.cast(AspireList[str], result)

    @_cached_property
    def metadata(self) -> AspireDict[str, str]:
        """Gets the Metadata property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata',
            {'context': self._handle}
        )
        return typing.cast(AspireDict[str, str], result)


class TestEnvironmentContext:
    """Type class for TestEnvironmentContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"TestEnvironmentContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def name(self) -> str:
        """Gets the Name property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.name',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @name.setter
    def name(self, value: str) -> None:
        """Sets the Name property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setName',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def description(self) -> str:
        """Gets the Description property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @description.setter
    def description(self, value: str) -> None:
        """Sets the Description property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setDescription',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def priority(self) -> int:
        """Gets the Priority property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.priority',
            {'context': self._handle}
        )
        return typing.cast(int, result)

    @priority.setter
    def priority(self, value: int) -> None:
        """Sets the Priority property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setPriority',
            {'context': self._handle, 'value': value}
        )


class TestMutableCollectionContext:
    """Type class for TestMutableCollectionContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"TestMutableCollectionContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def tags(self) -> AspireList[str]:
        """Gets the Tags property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.tags',
            {'context': self._handle}
        )
        return typing.cast(AspireList[str], result)

    @tags.setter
    def tags(self, value: AspireList[str]) -> None:
        """Sets the Tags property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.setTags',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def counts(self) -> AspireDict[str, int]:
        """Gets the Counts property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.counts',
            {'context': self._handle}
        )
        return typing.cast(AspireDict[str, int], result)

    @counts.setter
    def counts(self, value: AspireDict[str, int]) -> None:
        """Sets the Counts property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.setCounts',
            {'context': self._handle, 'value': value}
        )


class TestResourceContext:
    """Type class for TestResourceContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"TestResourceContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def name(self) -> str:
        """Gets the Name property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.name',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @name.setter
    def name(self, value: str) -> None:
        """Sets the Name property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setName',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def value(self) -> int:
        """Gets the Value property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.value',
            {'context': self._handle}
        )
        return typing.cast(int, result)

    @value.setter
    def value(self, value: int) -> None:
        """Sets the Value property"""
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValue',
            {'context': self._handle, 'value': value}
        )

    def get_value(self) -> str:
        """Invokes the GetValueAsync method"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync',
            rpc_args,
        )
        return result

    def set_value(self, value: str) -> None:
        """Invokes the SetValueAsync method"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['value'] = value
        self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValueAsync',
            rpc_args
        )

    def validate(self) -> bool:
        """Invokes the ValidateAsync method"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.validateAsync',
            rpc_args,
        )
        return result


# ============================================================================
# Interface Classes
# ============================================================================

class AbstractResource(abc.ABC):
    """Abstract base class for AbstractResource interface."""

    @abc.abstractmethod
    def with_optional_string(self, *, value: str | None = None, enabled: bool = True) -> typing.Self:
        """Adds an optional string parameter"""

    @abc.abstractmethod
    def with_config(self, config: TestConfigDto) -> typing.Self:
        """Configures the resource with a DTO"""

    @abc.abstractmethod
    def with_created_at(self, created_at: datetime.datetime) -> typing.Self:
        """Sets the created timestamp"""

    @abc.abstractmethod
    def with_modified_at(self, modified_at: datetime.datetime) -> typing.Self:
        """Sets the modified timestamp"""

    @abc.abstractmethod
    def with_correlation_id(self, correlation_id: str) -> typing.Self:
        """Sets the correlation ID"""

    @abc.abstractmethod
    def with_optional_callback(self, *, callback: typing.Callable[[TestCallbackContext], None] | None = None) -> typing.Self:
        """Configures with optional callback"""

    @abc.abstractmethod
    def with_status(self, status: TestResourceStatus) -> typing.Self:
        """Sets the resource status"""

    @abc.abstractmethod
    def with_nested_config(self, config: TestNestedDto) -> typing.Self:
        """Configures with nested DTO"""

    @abc.abstractmethod
    def with_validator(self, validator: typing.Callable[[TestResourceContext], bool]) -> typing.Self:
        """Adds validation callback"""

    @abc.abstractmethod
    def test_wait_for(self, dependency: AbstractResource) -> typing.Self:
        """Waits for another resource (test version)"""

    @abc.abstractmethod
    def with_dependency(self, dependency: AbstractResourceWithConnectionString) -> typing.Self:
        """Adds a dependency on another resource"""

    @abc.abstractmethod
    def with_union_dependency(self, dependency: str | AbstractResourceWithConnectionString) -> typing.Self:
        """Adds a dependency from a string or another resource"""

    @abc.abstractmethod
    def with_endpoints(self, endpoints: typing.Iterable[str]) -> typing.Self:
        """Sets the endpoints"""

    @abc.abstractmethod
    def with_cancellable_operation(self, operation: typing.Callable[[CancellationToken], None]) -> typing.Self:
        """Performs a cancellable operation"""

    @abc.abstractmethod
    def with_merge_label(self, label: str, *, category: str | None = None) -> typing.Self:
        """Adds a label to the resource"""

    @abc.abstractmethod
    def with_merge_endpoint(self, endpoint_name: str, port: int, *, scheme: str | None = None) -> typing.Self:
        """Configures a named endpoint"""

    @abc.abstractmethod
    def with_merge_logging(self, log_level: str, *, log_path: str | None = None, enable_console: bool = True, max_files: int | None = None) -> typing.Self:
        """Configures resource logging"""

    @abc.abstractmethod
    def with_merge_route(self, path: str, method: str, handler: str, priority: int, *, middleware: str | None = None) -> typing.Self:
        """Configures a route"""


class AbstractComputeResource(AbstractResource):
    """Abstract base class for AbstractComputeResource interface."""


class AbstractResourceWithArgs(AbstractResource):
    """Abstract base class for AbstractResourceWithArgs interface."""


class AbstractResourceWithConnectionString(AbstractResource, AbstractExpressionValue, AbstractValueWithReferences):
    """Abstract base class for AbstractResourceWithConnectionString interface."""

    @abc.abstractmethod
    def with_connection_string(self, connection_string: ReferenceExpression) -> typing.Self:
        """Sets the connection string using a reference expression"""

    @abc.abstractmethod
    def with_connection_string_direct(self, connection_string: str) -> typing.Self:
        """Sets connection string using direct interface target"""


class AbstractResourceWithEndpoints(AbstractResource):
    """Abstract base class for AbstractResourceWithEndpoints interface."""


class AbstractResourceWithEnvironment(AbstractResource):
    """Abstract base class for AbstractResourceWithEnvironment interface."""

    @abc.abstractmethod
    def test_with_env_callback(self, callback: typing.Callable[[TestEnvironmentContext], None]) -> typing.Self:
        """Configures environment with callback (test version)"""

    @abc.abstractmethod
    def with_env_vars(self, vars: typing.Mapping[str, str]) -> typing.Self:
        """Sets environment variables"""


class AbstractResourceWithProbes(AbstractResource):
    """Abstract base class for AbstractResourceWithProbes interface."""


class AbstractResourceWithWaitSupport(AbstractResource):
    """Abstract base class for AbstractResourceWithWaitSupport interface."""


class AbstractTestVaultResource(AbstractResource):
    """Abstract base class for AbstractTestVaultResource interface."""

    @abc.abstractmethod
    def with_vault_direct(self, option: str) -> typing.Self:
        """Configures vault using direct interface target"""


# ============================================================================
# Builder Classes
# ============================================================================

class _BaseResourceKwargs(typing.TypedDict, total=False):
    """Base resource options."""

    optional_string: OptionalStringParameters | typing.Literal[True]
    config: TestConfigDto
    created_at: datetime.datetime
    modified_at: datetime.datetime
    correlation_id: str
    optional_callback: typing.Callable[[TestCallbackContext], None] | typing.Literal[True]
    status: TestResourceStatus
    nested_config: TestNestedDto
    validator: typing.Callable[[TestResourceContext], bool]
    test_wait_for: AbstractResource
    dependency: AbstractResourceWithConnectionString
    union_dependency: str | AbstractResourceWithConnectionString
    endpoints: typing.Iterable[str]
    cancellable_operation: typing.Callable[[CancellationToken], None]
    merge_label: str | tuple[str, str]
    merge_endpoint: tuple[str, int] | MergeEndpointParameters
    merge_logging: str | MergeLoggingParameters
    merge_route: MergeRouteParameters

class _BaseResource(AbstractResource):
    """Base resource class."""

    def _wrap_builder(self, builder: typing.Any) -> Handle:
        if isinstance(builder, Handle):
            return builder
        return typing.cast(typing.Self, builder).handle

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def with_optional_string(self, *, value: str | None = None, enabled: bool = True) -> typing.Self:
        """Adds an optional string parameter"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if value is not None:
            rpc_args['value'] = value
        if enabled is not None:
            rpc_args['enabled'] = enabled
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_config(self, config: TestConfigDto) -> typing.Self:
        """Configures the resource with a DTO"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['config'] = config
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withConfig',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_created_at(self, created_at: datetime.datetime) -> typing.Self:
        """Sets the created timestamp"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['createdAt'] = created_at
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_modified_at(self, modified_at: datetime.datetime) -> typing.Self:
        """Sets the modified timestamp"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['modifiedAt'] = modified_at
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_correlation_id(self, correlation_id: str) -> typing.Self:
        """Sets the correlation ID"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['correlationId'] = correlation_id
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_optional_callback(self, *, callback: typing.Callable[[TestCallbackContext], None] | None = None) -> typing.Self:
        """Configures with optional callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if callback is not None:
            rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_status(self, status: TestResourceStatus) -> typing.Self:
        """Sets the resource status"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['status'] = status
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withStatus',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_nested_config(self, config: TestNestedDto) -> typing.Self:
        """Configures with nested DTO"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['config'] = config
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_validator(self, validator: typing.Callable[[TestResourceContext], bool]) -> typing.Self:
        """Adds validation callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['validator'] = self._client.register_callback(validator)
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withValidator',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def test_wait_for(self, dependency: AbstractResource) -> typing.Self:
        """Waits for another resource (test version)"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_dependency(self, dependency: AbstractResourceWithConnectionString) -> typing.Self:
        """Adds a dependency on another resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withDependency',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_union_dependency(self, dependency: str | AbstractResourceWithConnectionString) -> typing.Self:
        """Adds a dependency from a string or another resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withUnionDependency',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_endpoints(self, endpoints: typing.Iterable[str]) -> typing.Self:
        """Sets the endpoints"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['endpoints'] = endpoints
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_cancellable_operation(self, operation: typing.Callable[[CancellationToken], None]) -> typing.Self:
        """Performs a cancellable operation"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['operation'] = self._client.register_callback(operation)
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_merge_label(self, label: str, *, category: str | None = None) -> typing.Self:
        """Adds a label to the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['label'] = label
        if category is not None:
            rpc_args['category'] = category
        capability_id = 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLabelCategorized' if category is not None else 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLabel'
        result = self._client.invoke_capability(
            capability_id,
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_merge_endpoint(self, endpoint_name: str, port: int, *, scheme: str | None = None) -> typing.Self:
        """Configures a named endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['endpointName'] = endpoint_name
        rpc_args['port'] = port
        if scheme is not None:
            rpc_args['scheme'] = scheme
        capability_id = 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeEndpointScheme' if scheme is not None else 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeEndpoint'
        result = self._client.invoke_capability(
            capability_id,
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_merge_logging(self, log_level: str, *, log_path: str | None = None, enable_console: bool = True, max_files: int | None = None) -> typing.Self:
        """Configures resource logging"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['logLevel'] = log_level
        if log_path is not None:
            rpc_args['logPath'] = log_path
        if enable_console is not None:
            rpc_args['enableConsole'] = enable_console
        if max_files is not None:
            rpc_args['maxFiles'] = max_files
        capability_id = 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLoggingPath' if log_path is not None else 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLogging'
        result = self._client.invoke_capability(
            capability_id,
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_merge_route(self, path: str, method: str, handler: str, priority: int, *, middleware: str | None = None) -> typing.Self:
        """Configures a route"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['path'] = path
        rpc_args['method'] = method
        rpc_args['handler'] = handler
        rpc_args['priority'] = priority
        if middleware is not None:
            rpc_args['middleware'] = middleware
        capability_id = 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeRouteMiddleware' if middleware is not None else 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeRoute'
        result = self._client.invoke_capability(
            capability_id,
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[_BaseResourceKwargs]) -> None:
        if _optional_string := kwargs.pop("optional_string", None):
            if _validate_dict_types(_optional_string, OptionalStringParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["value"] = typing.cast(OptionalStringParameters, _optional_string).get("value")
                rpc_args["enabled"] = typing.cast(OptionalStringParameters, _optional_string).get("enabled")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString', rpc_args))
            elif _optional_string is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString', rpc_args))
            else:
                raise TypeError("Invalid type for option 'optional_string'. Expected: OptionalStringParameters or Literal[True]")
        if _config := kwargs.pop("config", None):
            if _validate_type(_config, TestConfigDto):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["config"] = typing.cast(TestConfigDto, _config)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withConfig', rpc_args))
            else:
                raise TypeError("Invalid type for option 'config'. Expected: TestConfigDto")
        if _created_at := kwargs.pop("created_at", None):
            if _validate_type(_created_at, datetime.datetime):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["createdAt"] = typing.cast(datetime.datetime, _created_at)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt', rpc_args))
            else:
                raise TypeError("Invalid type for option 'created_at'. Expected: datetime.datetime")
        if _modified_at := kwargs.pop("modified_at", None):
            if _validate_type(_modified_at, datetime.datetime):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["modifiedAt"] = typing.cast(datetime.datetime, _modified_at)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt', rpc_args))
            else:
                raise TypeError("Invalid type for option 'modified_at'. Expected: datetime.datetime")
        if _correlation_id := kwargs.pop("correlation_id", None):
            if _validate_type(_correlation_id, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["correlationId"] = typing.cast(str, _correlation_id)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId', rpc_args))
            else:
                raise TypeError("Invalid type for option 'correlation_id'. Expected: str")
        if _optional_callback := kwargs.pop("optional_callback", None):
            if _validate_type(_optional_callback, typing.Callable[[TestCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[TestCallbackContext], None], _optional_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback', rpc_args))
            elif _optional_callback is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'optional_callback'. Expected: Callable[[TestCallbackContext], None] or Literal[True]")
        if _status := kwargs.pop("status", None):
            if _validate_type(_status, TestResourceStatus):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["status"] = typing.cast(TestResourceStatus, _status)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withStatus', rpc_args))
            else:
                raise TypeError("Invalid type for option 'status'. Expected: TestResourceStatus")
        if _nested_config := kwargs.pop("nested_config", None):
            if _validate_type(_nested_config, TestNestedDto):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["config"] = typing.cast(TestNestedDto, _nested_config)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig', rpc_args))
            else:
                raise TypeError("Invalid type for option 'nested_config'. Expected: TestNestedDto")
        if _validator := kwargs.pop("validator", None):
            if _validate_type(_validator, typing.Callable[[TestResourceContext], bool]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["validator"] = client.register_callback(typing.cast(typing.Callable[[TestResourceContext], bool], _validator))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withValidator', rpc_args))
            else:
                raise TypeError("Invalid type for option 'validator'. Expected: Callable[[TestResourceContext], bool]")
        if _test_wait_for := kwargs.pop("test_wait_for", None):
            if _validate_type(_test_wait_for, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _test_wait_for)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor', rpc_args))
            else:
                raise TypeError("Invalid type for option 'test_wait_for'. Expected: AbstractResource")
        if _dependency := kwargs.pop("dependency", None):
            if _validate_type(_dependency, AbstractResourceWithConnectionString):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResourceWithConnectionString, _dependency)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withDependency', rpc_args))
            else:
                raise TypeError("Invalid type for option 'dependency'. Expected: AbstractResourceWithConnectionString")
        if _union_dependency := kwargs.pop("union_dependency", None):
            if _validate_type(_union_dependency, str | AbstractResourceWithConnectionString):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(str | AbstractResourceWithConnectionString, _union_dependency)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withUnionDependency', rpc_args))
            else:
                raise TypeError("Invalid type for option 'union_dependency'. Expected: str | AbstractResourceWithConnectionString")
        if _endpoints := kwargs.pop("endpoints", None):
            if _validate_type(_endpoints, typing.Iterable[str]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpoints"] = typing.cast(typing.Iterable[str], _endpoints)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints', rpc_args))
            else:
                raise TypeError("Invalid type for option 'endpoints'. Expected: Iterable[str]")
        if _cancellable_operation := kwargs.pop("cancellable_operation", None):
            if _validate_type(_cancellable_operation, typing.Callable[[CancellationToken], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["operation"] = client.register_callback(typing.cast(typing.Callable[[CancellationToken], None], _cancellable_operation))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation', rpc_args))
            else:
                raise TypeError("Invalid type for option 'cancellable_operation'. Expected: Callable[[CancellationToken], None]")
        if _merge_label := kwargs.pop("merge_label", None):
            if _validate_type(_merge_label, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["label"] = typing.cast(str, _merge_label)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLabel', rpc_args))
            elif _validate_tuple_types(_merge_label, (str, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["label"] = typing.cast(tuple[str, str], _merge_label)[0]
                rpc_args["category"] = typing.cast(tuple[str, str], _merge_label)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLabelCategorized', rpc_args))
            else:
                raise TypeError("Invalid type for option 'merge_label'. Expected: str or (str, str)")
        if _merge_endpoint := kwargs.pop("merge_endpoint", None):
            if _validate_tuple_types(_merge_endpoint, (str, int)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(tuple[str, int], _merge_endpoint)[0]
                rpc_args["port"] = typing.cast(tuple[str, int], _merge_endpoint)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withMergeEndpoint', rpc_args))
            elif _validate_dict_types(_merge_endpoint, MergeEndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(MergeEndpointParameters, _merge_endpoint)["endpoint_name"]
                rpc_args["port"] = typing.cast(MergeEndpointParameters, _merge_endpoint)["port"]
                rpc_args["scheme"] = typing.cast(MergeEndpointParameters, _merge_endpoint).get("scheme")
                capability_id = 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeEndpointScheme' if "scheme" in _merge_endpoint else 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeEndpoint'
                handle = self._wrap_builder(client.invoke_capability(capability_id, rpc_args))
            else:
                raise TypeError("Invalid type for option 'merge_endpoint'. Expected: (str, int) or MergeEndpointParameters")
        if _merge_logging := kwargs.pop("merge_logging", None):
            if _validate_type(_merge_logging, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["logLevel"] = typing.cast(str, _merge_logging)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLogging', rpc_args))
            elif _validate_dict_types(_merge_logging, MergeLoggingParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["logLevel"] = typing.cast(MergeLoggingParameters, _merge_logging)["log_level"]
                rpc_args["logPath"] = typing.cast(MergeLoggingParameters, _merge_logging).get("log_path")
                rpc_args["enableConsole"] = typing.cast(MergeLoggingParameters, _merge_logging).get("enable_console")
                rpc_args["maxFiles"] = typing.cast(MergeLoggingParameters, _merge_logging).get("max_files")
                capability_id = 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLoggingPath' if "log_path" in _merge_logging else 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeLogging'
                handle = self._wrap_builder(client.invoke_capability(capability_id, rpc_args))
            else:
                raise TypeError("Invalid type for option 'merge_logging'. Expected: str or MergeLoggingParameters")
        if _merge_route := kwargs.pop("merge_route", None):
            if _validate_dict_types(_merge_route, MergeRouteParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(MergeRouteParameters, _merge_route)["path"]
                rpc_args["method"] = typing.cast(MergeRouteParameters, _merge_route)["method"]
                rpc_args["handler"] = typing.cast(MergeRouteParameters, _merge_route)["handler"]
                rpc_args["priority"] = typing.cast(MergeRouteParameters, _merge_route)["priority"]
                rpc_args["middleware"] = typing.cast(MergeRouteParameters, _merge_route).get("middleware")
                capability_id = 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeRouteMiddleware' if "middleware" in _merge_route else 'Aspire.Hosting.CodeGeneration.Python.Tests/withMergeRoute'
                handle = self._wrap_builder(client.invoke_capability(capability_id, rpc_args))
            else:
                raise TypeError("Invalid type for option 'merge_route'. Expected: MergeRouteParameters")
        self._handle = handle
        self._client = client
        if kwargs:
            raise TypeError(f"Unexpected keyword arguments: {list(kwargs.keys())}")


class ContainerResourceKwargs(_BaseResourceKwargs, total=False):
    """ContainerResource options."""

    test_with_env_callback: typing.Callable[[TestEnvironmentContext], None]
    env_vars: typing.Mapping[str, str]

class ContainerResource(_BaseResource, AbstractResourceWithEnvironment, AbstractResourceWithArgs, AbstractResourceWithEndpoints, AbstractResourceWithWaitSupport, AbstractResourceWithProbes, AbstractComputeResource):
    """ContainerResource resource."""

    def __repr__(self) -> str:
        return "ContainerResource(handle={self._handle.handle_id})"

    def test_with_env_callback(self, callback: typing.Callable[[TestEnvironmentContext], None]) -> typing.Self:
        """Configures environment with callback (test version)"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_env_vars(self, vars: typing.Mapping[str, str]) -> typing.Self:
        """Sets environment variables"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['variables'] = vars
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[ContainerResourceKwargs]) -> None:
        if _test_with_env_callback := kwargs.pop("test_with_env_callback", None):
            if _validate_type(_test_with_env_callback, typing.Callable[[TestEnvironmentContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[TestEnvironmentContext], None], _test_with_env_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'test_with_env_callback'. Expected: Callable[[TestEnvironmentContext], None]")
        if _env_vars := kwargs.pop("env_vars", None):
            if _validate_type(_env_vars, typing.Mapping[str, str]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["variables"] = typing.cast(typing.Mapping[str, str], _env_vars)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables', rpc_args))
            else:
                raise TypeError("Invalid type for option 'env_vars'. Expected: Mapping[str, str]")
        super().__init__(handle, client, **kwargs)


class TestDatabaseResourceKwargs(ContainerResourceKwargs, total=False):
    """TestDatabaseResource options."""

    data_volume: str | typing.Literal[True]

class TestDatabaseResource(ContainerResource):
    """TestDatabaseResource resource."""

    def __repr__(self) -> str:
        return "TestDatabaseResource(handle={self._handle.handle_id})"

    def with_data_volume(self, *, name: str | None = None) -> typing.Self:
        """Adds a data volume"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if name is not None:
            rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[TestDatabaseResourceKwargs]) -> None:
        if _data_volume := kwargs.pop("data_volume", None):
            if _validate_type(_data_volume, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(str, _data_volume)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume', rpc_args))
            elif _data_volume is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume', rpc_args))
            else:
                raise TypeError("Invalid type for option 'data_volume'. Expected: str or Literal[True]")
        super().__init__(handle, client, **kwargs)


class TestRedisResourceKwargs(ContainerResourceKwargs, total=False):
    """TestRedisResource options."""

    persistence: TestPersistenceMode | typing.Literal[True]
    connection_string: ReferenceExpression
    connection_string_direct: str
    redis_specific: str
    multi_param_handle_callback: typing.Callable[[TestCallbackContext, TestEnvironmentContext], None]
    data_volume: DataVolumeParameters | typing.Literal[True]

class TestRedisResource(ContainerResource, AbstractResourceWithConnectionString):
    """TestRedisResource resource."""

    def __repr__(self) -> str:
        return "TestRedisResource(handle={self._handle.handle_id})"

    def add_test_child_database(self, name: str, *, database_name: str | None = None, **kwargs: typing.Unpack[TestDatabaseResourceKwargs]) -> TestDatabaseResource:  # type: ignore
        """Adds a child database to a test Redis resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        if database_name is not None:
            rpc_args['databaseName'] = database_name
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/addTestChildDatabase',
            rpc_args,
            kwargs,
        )
        return typing.cast(TestDatabaseResource, result)

    def with_persistence(self, *, mode: TestPersistenceMode = "Volume") -> typing.Self:
        """Configures the Redis resource with persistence"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if mode is not None:
            rpc_args['mode'] = mode
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withPersistence',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def get_tags(self) -> AspireList[str]:
        """Gets the tags for the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/getTags',
            rpc_args,
        )
        return typing.cast(AspireList[str], result)

    def get_metadata(self) -> AspireDict[str, str]:
        """Gets the metadata for the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/getMetadata',
            rpc_args,
        )
        return typing.cast(AspireDict[str, str], result)

    def with_connection_string(self, connection_string: ReferenceExpression) -> typing.Self:
        """Sets the connection string using a reference expression"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['connectionString'] = connection_string
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionString',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def get_endpoints(self) -> typing.Iterable[str]:
        """Gets the endpoints"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/getEndpoints',
            rpc_args,
        )
        return typing.cast(typing.Iterable[str], result)

    def with_connection_string_direct(self, connection_string: str) -> typing.Self:
        """Sets connection string using direct interface target"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['connectionString'] = connection_string
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionStringDirect',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_redis_specific(self, option: str) -> typing.Self:
        """Redis-specific configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['option'] = option
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withRedisSpecific',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def get_status(self, *, timeout: int | None = None) -> str:
        """Gets the status of the resource asynchronously"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/getStatusAsync',
            rpc_args,
        )
        return typing.cast(str, result)

    def wait_for_ready(self, timeout: float) -> bool:
        """Waits for the resource to be ready"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['timeout'] = timeout
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/waitForReadyAsync',
            rpc_args,
        )
        return typing.cast(bool, result)

    def with_multi_param_handle_callback(self, callback: typing.Callable[[TestCallbackContext, TestEnvironmentContext], None]) -> typing.Self:
        """Tests multi-param callback destructuring"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withMultiParamHandleCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_data_volume(self, *, name: str | None = None, is_read_only: bool = False) -> typing.Self:
        """Adds a data volume with persistence"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if name is not None:
            rpc_args['name'] = name
        if is_read_only is not None:
            rpc_args['isReadOnly'] = is_read_only
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[TestRedisResourceKwargs]) -> None:
        if _persistence := kwargs.pop("persistence", None):
            if _validate_type(_persistence, TestPersistenceMode):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["mode"] = typing.cast(TestPersistenceMode, _persistence)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withPersistence', rpc_args))
            elif _persistence is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withPersistence', rpc_args))
            else:
                raise TypeError("Invalid type for option 'persistence'. Expected: TestPersistenceMode or Literal[True]")
        if _connection_string := kwargs.pop("connection_string", None):
            if _validate_type(_connection_string, ReferenceExpression):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["connectionString"] = typing.cast(ReferenceExpression, _connection_string)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionString', rpc_args))
            else:
                raise TypeError("Invalid type for option 'connection_string'. Expected: ReferenceExpression")
        if _connection_string_direct := kwargs.pop("connection_string_direct", None):
            if _validate_type(_connection_string_direct, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["connectionString"] = typing.cast(str, _connection_string_direct)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionStringDirect', rpc_args))
            else:
                raise TypeError("Invalid type for option 'connection_string_direct'. Expected: str")
        if _redis_specific := kwargs.pop("redis_specific", None):
            if _validate_type(_redis_specific, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["option"] = typing.cast(str, _redis_specific)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withRedisSpecific', rpc_args))
            else:
                raise TypeError("Invalid type for option 'redis_specific'. Expected: str")
        if _multi_param_handle_callback := kwargs.pop("multi_param_handle_callback", None):
            if _validate_type(_multi_param_handle_callback, typing.Callable[[TestCallbackContext, TestEnvironmentContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[TestCallbackContext, TestEnvironmentContext], None], _multi_param_handle_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withMultiParamHandleCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'multi_param_handle_callback'. Expected: Callable[[TestCallbackContext, TestEnvironmentContext], None]")
        if _data_volume := kwargs.pop("data_volume", None):
            if _validate_dict_types(_data_volume, DataVolumeParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(DataVolumeParameters, _data_volume).get("name")
                rpc_args["isReadOnly"] = typing.cast(DataVolumeParameters, _data_volume).get("is_read_only")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume', rpc_args))
            elif _data_volume is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume', rpc_args))
            else:
                raise TypeError("Invalid type for option 'data_volume'. Expected: DataVolumeParameters or Literal[True]")
        super().__init__(handle, client, **kwargs)


class TestVaultResourceKwargs(ContainerResourceKwargs, total=False):
    """TestVaultResource options."""

    vault_direct: str

class TestVaultResource(ContainerResource, AbstractTestVaultResource):
    """TestVaultResource resource."""

    def __repr__(self) -> str:
        return "TestVaultResource(handle={self._handle.handle_id})"

    def with_vault_direct(self, option: str) -> typing.Self:
        """Configures vault using direct interface target"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['option'] = option
        result = self._client.invoke_capability(
            'Aspire.Hosting.CodeGeneration.Python.Tests/withVaultDirect',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[TestVaultResourceKwargs]) -> None:
        if _vault_direct := kwargs.pop("vault_direct", None):
            if _validate_type(_vault_direct, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["option"] = typing.cast(str, _vault_direct)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting.CodeGeneration.Python.Tests/withVaultDirect', rpc_args))
            else:
                raise TypeError("Invalid type for option 'vault_direct'. Expected: str")
        super().__init__(handle, client, **kwargs)


# ============================================================================
# Connection Helper
# ============================================================================

def _get_client(*, debug: bool, heartbeat_interval: int | None) -> AspireClient:
    '''
    Creates and connects to the Aspire AppHost.
    Reads connection info from environment variables set by `aspire run`.
    '''
    socket_path = os.environ.get('REMOTE_APP_HOST_SOCKET_PATH')
    if not socket_path:
        raise ValueError(
            'REMOTE_APP_HOST_SOCKET_PATH environment variable not set. '
            'Run this application using `aspire run`.'
        )

    client = AspireClient(socket_path, debug=debug, heartbeat_interval=heartbeat_interval)
    client.connect()
    auth_token = os.environ.get('ASPIRE_REMOTE_APPHOST_TOKEN')
    if not auth_token:
        raise ValueError(
            'ASPIRE_REMOTE_APPHOST_TOKEN environment variable not set. '
            'Run this application using `aspire run`.'
        )
    client.authenticate(auth_token)
    return client


def create_builder(
    *,
    args: typing.Iterable[str] | None = None,
    project_directory: str | None = None,
    container_registry_override: str | None = None,
    disable_dashboard: bool | None = None,
    dashboard_application_name: str | None = None,
    allow_unsecured_transport: bool | None = None,
    enable_resource_logging: bool | None = None,
    options: CreateBuilderOptions | None = None,
    debug: bool | None = None,
    heartbeat_interval: int | None = None,
 ) -> AbstractContextManager[DistributedApplicationBuilder]:
    '''
    Creates a new distributed application builder.
    This is the entry point for building Aspire applications.

    Args:
        args (Iterable[str]): Command-line arguments to pass to the AppHost. By default, this will be set to any additional arguments
            passed to the Aspire command line (arguments specified after '--'). Specifying them here will override that default.
        project_directory (str): The directory containing the AppHost project file. By default, this will  use the ASPIRE_PROJECT_DIRECTORY
            environment variable if set, otherwise it will use the current working directory.
        container_registry_override (str): When containers are used, use this value to override the container registry.
        disable_dashboard (bool): Determines whether the dashboard is disabled.
        dashboard_application_name (str): The application name to display in the dashboard.
        allow_unsecured_transport (bool): Allows the use of HTTP urls for the AppHost resource endpoint.
        enable_resource_logging (bool): Enables resource logging.
        options (CreateBuilderOptions): An optional dict containing any of the above options. Specifying options here will override default behaviours,
           but individual parameters will take precedence.
        debug (bool): Whether to enable logging of the communication between the client and AppHost server.
            Default behaviour will be determined by whether `--debug` is passed as an Aspire command-line argument, or
            if the ASPIRE_DEBUG environment variable is set. Enabling or disabling here will override those defaults.
            Messages will be logged as INFO, with the 'aspire_app' logger name (connection heartbeat messages will be logged at DEBUG).
        heartbeat_interval (int): Optional interval in seconds for sending heartbeat messages to the AppHost. Default value is 5 seconds.

    Returns:
        A DistributedApplicationBuilder instance
    '''
    is_debug = debug if debug is not None else os.environ.get('ASPIRE_DEBUG', 'false').lower() == 'true'
    client = _get_client(debug=is_debug, heartbeat_interval=heartbeat_interval)

    # Default args and project_directory if not provided
    effective_options = options or CreateBuilderOptions()
    if args is not None:
        effective_options['Args'] = args
    elif not effective_options.get('Args'):
        effective_options['Args'] = sys.argv[1:]
    if project_directory is not None:
        effective_options['ProjectDirectory'] = project_directory
    elif not effective_options.get('ProjectDirectory'):
        effective_options['ProjectDirectory'] = os.environ.get('ASPIRE_PROJECT_DIRECTORY', os.getcwd())
    if container_registry_override is not None:
        effective_options['ContainerRegistryOverride'] = container_registry_override
    if disable_dashboard is not None:
        effective_options['DisableDashboard'] = disable_dashboard
    if dashboard_application_name is not None:
        effective_options['DashboardApplicationName'] = dashboard_application_name
    if allow_unsecured_transport is not None:
        effective_options['AllowUnsecuredTransport'] = allow_unsecured_transport
    if enable_resource_logging is not None:
        effective_options['EnableResourceLogging'] = enable_resource_logging

    return DistributedApplicationBuilder(client, effective_options)


# ============================================================================
# Handle Registrations
# ============================================================================

_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", lambda handle, _: ReferenceExpression(handle))
_register_handle_wrapper("System.Private.CoreLib/System.Threading.CancellationToken", CancellationToken)
_register_handle_wrapper("Aspire.Hosting/List<string>", AspireList)
_register_handle_wrapper("Aspire.Hosting/Dict<string,string>", AspireDict)
_register_handle_wrapper("Aspire.Hosting/Dict<string,number>", AspireDict)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", TestCallbackContext)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext", TestCollectionContext)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext", TestEnvironmentContext)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestMutableCollectionContext", TestMutableCollectionContext)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext", TestResourceContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.Resource", _BaseResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource", ContainerResource)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource", TestDatabaseResource)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource", TestRedisResource)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource", TestVaultResource)
