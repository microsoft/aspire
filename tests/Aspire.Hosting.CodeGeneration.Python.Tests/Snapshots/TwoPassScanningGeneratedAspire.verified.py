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

CertificateTrustScope = typing.Literal["None", "Append", "Override", "System"]

CommandResultFormat = typing.Literal["Text", "Json", "Markdown"]

ContainerLifetime = typing.Literal["Session", "Persistent"]

DistributedApplicationOperation = typing.Literal["Run", "Publish"]

EndpointProperty = typing.Literal["Url", "Host", "IPV4Host", "Port", "Scheme", "TargetPort", "HostAndPort", "TlsEnabled"]

HttpCommandResultMode = typing.Literal["None", "Auto", "Json", "Text"]

IconVariant = typing.Literal["Regular", "Filled"]

ImagePullPolicy = typing.Literal["Default", "Always", "Missing", "Never"]

OtlpProtocol = typing.Literal["Grpc", "HttpProtobuf", "HttpJson"]

ProbeType = typing.Literal["Startup", "Readiness", "Liveness"]

ProtocolType = typing.Literal["IP", "IPv6HopByHopOptions", "Unspecified", "Icmp", "Igmp", "Ggp", "IPv4", "Tcp", "Pup", "Udp", "Idp", "IPv6", "IPv6RoutingHeader", "IPv6FragmentHeader", "IPSecEncapsulatingSecurityPayload", "IPSecAuthenticationHeader", "IcmpV6", "IPv6NoNextHeader", "IPv6DestinationOptions", "ND", "Raw", "Ipx", "Spx", "SpxII", "Unknown"]

TestPersistenceMode = typing.Literal["None", "Volume", "Bind"]

TestResourceStatus = typing.Literal["Pending", "Running", "Stopped", "Failed"]

UrlDisplayLocation = typing.Literal["SummaryAndDetails", "DetailsOnly"]

WaitBehavior = typing.Literal["WaitOnResourceUnavailable", "StopOnResourceUnavailable"]


# ============================================================================
# Method Parameters
# ============================================================================


class DockerfileBaseImageParameters(typing.TypedDict, total=False):
    build_image: str
    runtime_image: str


class CommandParameters(typing.TypedDict, total=False):
    name: typing.Required[str]
    display_name: typing.Required[str]
    execute_command: typing.Required[typing.Callable[[ExecuteCommandContext], ExecuteCommandResult]]
    command_options: CommandOptions


class PipelineStepFactoryParameters(typing.TypedDict, total=False):
    step_name: typing.Required[str]
    callback: typing.Required[typing.Callable[[PipelineStepContext], None]]
    depends_on: typing.Iterable[str]
    required_by: typing.Iterable[str]
    tags: typing.Iterable[str]
    description: str


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


class BindMountParameters(typing.TypedDict, total=False):
    source: typing.Required[str]
    target: typing.Required[str]
    is_read_only: bool


class DockerfileParameters(typing.TypedDict, total=False):
    context_path: typing.Required[str]
    dockerfile_path: str
    stage: str


class ContainerCertificatePathsParameters(typing.TypedDict, total=False):
    custom_certificates_destination: str
    default_certificate_bundle_paths: typing.Iterable[str]
    default_certificate_dir_paths: typing.Iterable[str]


class DockerfileBuilderParameters(typing.TypedDict, total=False):
    context_path: typing.Required[str]
    callback: typing.Required[typing.Callable[[DockerfileBuilderCallbackContext], None]]
    stage: str


class McpServerParameters(typing.TypedDict, total=False):
    path: str
    endpoint_name: str


class ReferenceParameters(typing.TypedDict, total=False):
    source: typing.Required[AbstractResource | EndpointReference | str]
    connection_name: str
    optional: bool
    name: str


class EndpointCallbackParameters(typing.TypedDict, total=False):
    endpoint_name: typing.Required[str]
    callback: typing.Required[typing.Callable[[EndpointUpdateContext], None]]
    create_if_not_exists: bool


class HttpEndpointCallbackParameters(typing.TypedDict, total=False):
    callback: typing.Required[typing.Callable[[EndpointUpdateContext], None]]
    name: str
    create_if_not_exists: bool


class HttpsEndpointCallbackParameters(typing.TypedDict, total=False):
    callback: typing.Required[typing.Callable[[EndpointUpdateContext], None]]
    name: str
    create_if_not_exists: bool


class EndpointParameters(typing.TypedDict, total=False):
    port: int
    target_port: int
    scheme: str
    name: str
    env: str
    is_proxied: bool
    is_external: bool
    protocol: ProtocolType


class HttpEndpointParameters(typing.TypedDict, total=False):
    port: int
    target_port: int
    name: str
    env: str
    is_proxied: bool


class HttpsEndpointParameters(typing.TypedDict, total=False):
    port: int
    target_port: int
    name: str
    env: str
    is_proxied: bool


class HttpHealthCheckParameters(typing.TypedDict, total=False):
    path: str
    status_code: int
    endpoint_name: str


class HttpCommandParameters(typing.TypedDict, total=False):
    path: typing.Required[str]
    display_name: typing.Required[str]
    options: HttpCommandExportOptions


class HttpProbeParameters(typing.TypedDict, total=False):
    probe_type: typing.Required[ProbeType]
    path: str
    initial_delay_seconds: int
    period_seconds: int
    timeout_seconds: int
    failure_threshold: int
    success_threshold: int
    endpoint_name: str


class VolumeParameters(typing.TypedDict, total=False):
    target: typing.Required[str]
    name: str
    is_read_only: bool


class DataVolumeParameters(typing.TypedDict, total=False):
    name: str
    is_read_only: bool

# ============================================================================
# DTO Classes (Data Transfer Objects)
# ============================================================================

class AddContainerOptions(typing.TypedDict, total=False):
    Image: str
    Tag: str

class CertificateTrustExecutionConfigurationContext(typing.TypedDict, total=False):
    CertificateBundlePath: ReferenceExpression
    CertificateDirectoriesPath: ReferenceExpression
    RootCertificatesPath: str
    IsContainer: bool

class CertificateTrustExecutionConfigurationExportData(typing.TypedDict, total=False):
    Scope: CertificateTrustScope
    CertificateSubjects: typing.Iterable[str]
    CustomBundlePaths: typing.Iterable[str]

class CommandOptions(typing.TypedDict, total=False):
    Description: str
    Parameter: typing.Any
    ConfirmationMessage: str
    IconName: str
    IconVariant: IconVariant
    IsHighlighted: bool
    UpdateState: typing.Any

class CommandResultData(typing.TypedDict, total=False):
    Value: str
    Format: CommandResultFormat
    DisplayImmediately: bool

class CreateBuilderOptions(typing.TypedDict, total=False):
    Args: typing.Iterable[str]
    ProjectDirectory: str
    AppHostFilePath: str
    ContainerRegistryOverride: str
    DisableDashboard: bool
    DashboardApplicationName: str
    AllowUnsecuredTransport: bool
    EnableResourceLogging: bool

class ExecuteCommandResult(typing.TypedDict, total=False):
    Success: bool
    Canceled: bool
    ErrorMessage: str
    Message: str
    Data: CommandResultData

class GenerateParameterDefault(typing.TypedDict, total=False):
    MinLength: int
    Lower: bool
    Upper: bool
    Numeric: bool
    Special: bool
    MinLower: int
    MinUpper: int
    MinNumeric: int
    MinSpecial: int

class HttpCommandExportOptions(typing.TypedDict, total=False):
    Description: str
    ConfirmationMessage: str
    IconName: str
    IconVariant: IconVariant
    IsHighlighted: bool
    CommandName: str
    EndpointName: str
    MethodName: str
    ResultMode: HttpCommandResultMode

class HttpsCertificateExecutionConfigurationContext(typing.TypedDict, total=False):
    CertificatePath: ReferenceExpression
    KeyPath: ReferenceExpression
    PfxPath: ReferenceExpression

class HttpsCertificateExecutionConfigurationExportData(typing.TypedDict, total=False):
    Subject: str
    Thumbprint: str
    KeyPathExpression: str
    PfxPathExpression: str
    IsKeyPathReferenced: bool
    IsPfxPathReferenced: bool
    Password: str

class HttpsCertificateInfo(typing.TypedDict, total=False):
    Subject: str
    Issuer: str
    Thumbprint: str

class ReferenceEnvironmentInjectionOptions(typing.TypedDict, total=False):
    ConnectionString: bool
    ConnectionProperties: bool
    ServiceDiscovery: bool
    Endpoints: bool

class ResourceEventDto(typing.TypedDict, total=False):
    ResourceName: str
    ResourceId: str
    State: str
    StateStyle: str
    HealthStatus: str
    ExitCode: int

class ResourceUrlAnnotation(typing.TypedDict, total=False):
    Url: str
    DisplayText: str
    Endpoint: EndpointReference
    DisplayLocation: UrlDisplayLocation

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

WellKnownPipelineSteps = types.SimpleNamespace()
# The step that runs before the application starts.
WellKnownPipelineSteps.BeforeStart = "before-start"
# The well-known step for building resources.
WellKnownPipelineSteps.Build = "build"
# The prerequisite step that runs before any build operations.
WellKnownPipelineSteps.BuildPrereq = "build-prereq"
# The step that checks whether the container runtime (e.g., Docker or Podman) is running. Build steps that need a container runtime should depend on this step.
WellKnownPipelineSteps.CheckContainerRuntime = "check-container-runtime"
# Aggregation step for all deploy operations. All deploy steps should be required by this step.
WellKnownPipelineSteps.Deploy = "deploy"
# The prerequisite step that runs before any deploy operations.
WellKnownPipelineSteps.DeployPrereq = "deploy-prereq"
# Aggregation step for all destroy operations. All destroy steps should be required by this step.
WellKnownPipelineSteps.Destroy = "destroy"
# The prerequisite step that runs before any destroy operations.
WellKnownPipelineSteps.DestroyPrereq = "destroy-prereq"
# The diagnostic step that dumps dependency graph information for troubleshooting.
WellKnownPipelineSteps.Diagnostics = "diagnostics"
# The step that prompts for parameter values before build, publish, or deployment operations.
WellKnownPipelineSteps.ProcessParameters = "process-parameters"
# Aggregation step for all publish operations. All publish steps should be required by this step.
WellKnownPipelineSteps.Publish = "publish"
# The prerequisite step that runs before any publish operations.
WellKnownPipelineSteps.PublishPrereq = "publish-prereq"
# The meta-step that coordinates all push operations. All push steps should be required by this step.
WellKnownPipelineSteps.Push = "push"
# The prerequisite step that runs before any push operations.
WellKnownPipelineSteps.PushPrereq = "push-prereq"
# The step that validates compute resources are assigned to unambiguous compute environments.
WellKnownPipelineSteps.ValidateComputeEnvironments = "validate-compute-environments"

WellKnownPipelineTags = types.SimpleNamespace()
# Tag for steps that build compute resources.
WellKnownPipelineTags.BuildCompute = "build-compute"
# Tag for steps that deploy to compute infrastructure.
WellKnownPipelineTags.DeployCompute = "deploy-compute"
# Tag for steps that provision infrastructure resources.
WellKnownPipelineTags.ProvisionInfrastructure = "provision-infra"
# Tag for steps that push container images to a registry.
WellKnownPipelineTags.PushContainerImage = "push-container-image"


# ============================================================================
# Type Classes
# ============================================================================

class AbstractAspireStore:
    """Type class for AbstractAspireStore."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractAspireStore(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def get_file_name_with_content(self, filename_template: str, source_filename: str) -> str:
        """Gets a deterministic file path for the specified file contents"""
        rpc_args: dict[str, typing.Any] = {'aspireStore': self._handle}
        rpc_args['filenameTemplate'] = filename_template
        rpc_args['sourceFilename'] = source_filename
        result = self._client.invoke_capability(
            'Aspire.Hosting/getFileNameWithContent',
            rpc_args,
        )
        return result


class AbstractConfiguration:
    """Type class for AbstractConfiguration."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractConfiguration(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def get_config_value(self, key: str) -> str:
        """Gets a configuration value by key"""
        rpc_args: dict[str, typing.Any] = {'configuration': self._handle}
        rpc_args['key'] = key
        result = self._client.invoke_capability(
            'Aspire.Hosting/getConfigValue',
            rpc_args,
        )
        return result

    def get_connection_string(self, name: str) -> str:
        """Gets a connection string by name"""
        rpc_args: dict[str, typing.Any] = {'configuration': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/getConnectionString',
            rpc_args,
        )
        return result

    def get_section(self, key: str) -> AbstractConfigurationSection:
        """Gets a configuration section by key"""
        rpc_args: dict[str, typing.Any] = {'configuration': self._handle}
        rpc_args['key'] = key
        result = self._client.invoke_capability(
            'Aspire.Hosting/getSection',
            rpc_args,
        )
        return typing.cast(AbstractConfigurationSection, result)

    def get_children(self) -> typing.Iterable[AbstractConfigurationSection]:
        """Gets child configuration sections"""
        rpc_args: dict[str, typing.Any] = {'configuration': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getChildren',
            rpc_args,
        )
        return result

    def exists(self, key: str) -> bool:
        """Checks whether a configuration section exists"""
        rpc_args: dict[str, typing.Any] = {'configuration': self._handle}
        rpc_args['key'] = key
        result = self._client.invoke_capability(
            'Aspire.Hosting/exists',
            rpc_args,
        )
        return result


class AbstractConfigurationSection(abc.ABC):
    """Abstract base class for AbstractConfigurationSection."""

class AbstractContainerRegistry(abc.ABC):
    """Abstract base class for AbstractContainerRegistry."""

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

    @_cached_property
    def app_host_dir(self) -> str:
        """Gets the AppHostDirectory property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/IDistributedApplicationBuilder.appHostDirectory',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @_cached_property
    def env(self) -> AbstractHostEnvironment:
        """Gets the Environment property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/IDistributedApplicationBuilder.environment',
            {'context': self._handle}
        )
        return typing.cast(AbstractHostEnvironment, result)

    @_cached_property
    def eventing(self) -> AbstractDistributedApplicationEventing:
        """Gets the Eventing property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/IDistributedApplicationBuilder.eventing',
            {'context': self._handle}
        )
        return typing.cast(AbstractDistributedApplicationEventing, result)

    @_cached_property
    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/IDistributedApplicationBuilder.executionContext',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationExecutionContext, result)

    @_cached_property
    def pipeline(self) -> AbstractDistributedApplicationPipeline:
        """Gets the Pipeline property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/IDistributedApplicationBuilder.pipeline',
            {'context': self._handle}
        )
        return typing.cast(AbstractDistributedApplicationPipeline, result)

    @_cached_property
    def user_secrets_manager(self) -> AbstractUserSecretsManager:
        """Gets the UserSecretsManager property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/IDistributedApplicationBuilder.userSecretsManager',
            {'context': self._handle}
        )
        return typing.cast(AbstractUserSecretsManager, result)

    def build(self) -> DistributedApplication:
        """Builds the distributed application"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/build',
            rpc_args,
        )
        return typing.cast(DistributedApplication, result)

    def add_container_registry(self, name: str, endpoint: str | ParameterResource, *, repository: str | ParameterResource | None = None, **kwargs: typing.Unpack["ContainerRegistryResourceKwargs"]) -> ContainerRegistryResource:  # type: ignore
        """Adds a container registry resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['endpoint'] = endpoint
        if repository is not None:
            rpc_args['repository'] = repository
        result = self._client.invoke_capability(
            'Aspire.Hosting/addContainerRegistry',
            rpc_args,
            kwargs,
        )
        return typing.cast(ContainerRegistryResource, result)

    def add_container(self, name: str, image: str | AddContainerOptions, **kwargs: typing.Unpack["ContainerResourceKwargs"]) -> ContainerResource:  # type: ignore
        """Adds a container resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['image'] = image
        result = self._client.invoke_capability(
            'Aspire.Hosting/addContainer',
            rpc_args,
            kwargs,
        )
        return typing.cast(ContainerResource, result)

    def add_dockerfile(self, name: str, context_path: str, *, dockerfile_path: str | None = None, stage: str | None = None, **kwargs: typing.Unpack["ContainerResourceKwargs"]) -> ContainerResource:  # type: ignore
        """Adds a container resource built from a Dockerfile"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['contextPath'] = context_path
        if dockerfile_path is not None:
            rpc_args['dockerfilePath'] = dockerfile_path
        if stage is not None:
            rpc_args['stage'] = stage
        result = self._client.invoke_capability(
            'Aspire.Hosting/addDockerfile',
            rpc_args,
            kwargs,
        )
        return typing.cast(ContainerResource, result)

    def add_dockerfile_builder(self, name: str, context_path: str, callback: typing.Callable[[DockerfileBuilderCallbackContext], None], *, stage: str | None = None, **kwargs: typing.Unpack["ContainerResourceKwargs"]) -> ContainerResource:  # type: ignore
        """Adds a container resource built from a programmatically generated Dockerfile"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['contextPath'] = context_path
        rpc_args['callback'] = self._client.register_callback(callback)
        if stage is not None:
            rpc_args['stage'] = stage
        result = self._client.invoke_capability(
            'Aspire.Hosting/addDockerfileBuilder',
            rpc_args,
            kwargs,
        )
        return typing.cast(ContainerResource, result)

    def add_dotnet_tool(self, name: str, package_id: str, **kwargs: typing.Unpack["DotnetToolResourceKwargs"]) -> DotnetToolResource:  # type: ignore
        """Adds a .NET tool resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['packageId'] = package_id
        result = self._client.invoke_capability(
            'Aspire.Hosting/addDotnetTool',
            rpc_args,
            kwargs,
        )
        return typing.cast(DotnetToolResource, result)

    def add_executable(self, name: str, command: str, working_dir: str, args: typing.Iterable[str], **kwargs: typing.Unpack["ExecutableResourceKwargs"]) -> ExecutableResource:  # type: ignore
        """Adds an executable resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['command'] = command
        rpc_args['workingDirectory'] = working_dir
        rpc_args['args'] = args
        result = self._client.invoke_capability(
            'Aspire.Hosting/addExecutable',
            rpc_args,
            kwargs,
        )
        return typing.cast(ExecutableResource, result)

    def add_external_service(self, name: str, url: str | ParameterResource, **kwargs: typing.Unpack["ExternalServiceResourceKwargs"]) -> ExternalServiceResource:  # type: ignore
        """Adds an external service resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['url'] = url
        result = self._client.invoke_capability(
            'Aspire.Hosting/addExternalService',
            rpc_args,
            kwargs,
        )
        return typing.cast(ExternalServiceResource, result)

    def add_parameter(self, name: str, *, value: str | None = None, publish_value_as_default: bool = False, secret: bool = False, **kwargs: typing.Unpack["ParameterResourceKwargs"]) -> ParameterResource:  # type: ignore
        """Adds a parameter resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        if value is not None:
            rpc_args['value'] = value
        if publish_value_as_default is not None:
            rpc_args['publishValueAsDefault'] = publish_value_as_default
        if secret is not None:
            rpc_args['secret'] = secret
        result = self._client.invoke_capability(
            'Aspire.Hosting/addParameter',
            rpc_args,
            kwargs,
        )
        return typing.cast(ParameterResource, result)

    def add_parameter_from_config(self, name: str, config_key: str, *, secret: bool = False, **kwargs: typing.Unpack["ParameterResourceKwargs"]) -> ParameterResource:  # type: ignore
        """Adds a parameter sourced from configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['configurationKey'] = config_key
        if secret is not None:
            rpc_args['secret'] = secret
        result = self._client.invoke_capability(
            'Aspire.Hosting/addParameterFromConfiguration',
            rpc_args,
            kwargs,
        )
        return typing.cast(ParameterResource, result)

    def add_parameter_with_generated_value(self, name: str, value: GenerateParameterDefault, *, secret: bool = False, persist: bool = False, **kwargs: typing.Unpack["ParameterResourceKwargs"]) -> ParameterResource:  # type: ignore
        """Adds a parameter with a generated default value"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        if secret is not None:
            rpc_args['secret'] = secret
        if persist is not None:
            rpc_args['persist'] = persist
        result = self._client.invoke_capability(
            'Aspire.Hosting/addParameterWithGeneratedValue',
            rpc_args,
            kwargs,
        )
        return typing.cast(ParameterResource, result)

    def add_connection_string(self, name: str, *, env_var_name_or_expression: str | ReferenceExpression | None = None) -> AbstractResourceWithConnectionString:
        """Adds a connection string resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        if env_var_name_or_expression is not None:
            rpc_args['environmentVariableNameOrExpression'] = env_var_name_or_expression
        result = self._client.invoke_capability(
            'Aspire.Hosting/addConnectionString',
            rpc_args,
        )
        return typing.cast(AbstractResourceWithConnectionString, result)

    def add_project(self, name: str, project_path: str, *, launch_profile_or_options: str | ProjectResourceOptions | None = None, **kwargs: typing.Unpack["ProjectResourceKwargs"]) -> ProjectResource:  # type: ignore
        """Adds a .NET project resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['projectPath'] = project_path
        if launch_profile_or_options is not None:
            rpc_args['launchProfileOrOptions'] = launch_profile_or_options
        result = self._client.invoke_capability(
            'Aspire.Hosting/addProject',
            rpc_args,
            kwargs,
        )
        return typing.cast(ProjectResource, result)

    def add_c_sharp_app(self, name: str, path: str, *, options: ProjectResourceOptions | None = None, **kwargs: typing.Unpack["CSharpAppResourceKwargs"]) -> CSharpAppResource:  # type: ignore
        """Adds a C# application resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['path'] = path
        if options is not None:
            rpc_args['options'] = options
        result = self._client.invoke_capability(
            'Aspire.Hosting/addCSharpApp',
            rpc_args,
            kwargs,
        )
        return typing.cast(CSharpAppResource, result)

    def get_config(self) -> AbstractConfiguration:
        """Gets the application configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getConfiguration',
            rpc_args,
        )
        return typing.cast(AbstractConfiguration, result)

    def subscribe_before_start(self, callback: typing.Callable[[BeforeStartEvent], None]) -> DistributedApplicationEventSubscription:
        """Subscribes to the BeforeStart event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/subscribeBeforeStart',
            rpc_args,
        )
        return typing.cast(DistributedApplicationEventSubscription, result)

    def subscribe_after_resources_created(self, callback: typing.Callable[[AfterResourcesCreatedEvent], None]) -> DistributedApplicationEventSubscription:
        """Subscribes to the AfterResourcesCreated event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/subscribeAfterResourcesCreated',
            rpc_args,
        )
        return typing.cast(DistributedApplicationEventSubscription, result)

    def add_eventing_subscriber(self, subscribe: typing.Callable[[EventingSubscriberRegistrationContext], None]) -> None:
        """Adds an eventing subscriber"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['subscribe'] = self._client.register_callback(subscribe)
        self._client.invoke_capability(
            'Aspire.Hosting/addEventingSubscriber',
            rpc_args
        )

    def try_add_eventing_subscriber(self, subscribe: typing.Callable[[EventingSubscriberRegistrationContext], None]) -> None:
        """Attempts to add an eventing subscriber"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['subscribe'] = self._client.register_callback(subscribe)
        self._client.invoke_capability(
            'Aspire.Hosting/tryAddEventingSubscriber',
            rpc_args
        )

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


class AbstractDistributedApplicationEvent(abc.ABC):
    """Abstract base class for AbstractDistributedApplicationEvent."""

class AbstractDistributedApplicationEventing:
    """Type class for AbstractDistributedApplicationEventing."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractDistributedApplicationEventing(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def unsubscribe(self, subscription: DistributedApplicationEventSubscription) -> None:
        """Invokes the Unsubscribe method"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['subscription'] = subscription
        self._client.invoke_capability(
            'Aspire.Hosting.Eventing/IDistributedApplicationEventing.unsubscribe',
            rpc_args
        )


class AbstractDistributedApplicationPipeline:
    """Type class for AbstractDistributedApplicationPipeline."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractDistributedApplicationPipeline(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def disable_build_only_container_validation(self) -> AbstractDistributedApplicationPipeline:
        """Disables publish and deploy validation for unconsumed build-only containers."""
        rpc_args: dict[str, typing.Any] = {'pipeline': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/disableBuildOnlyContainerValidation',
            rpc_args,
        )
        return typing.cast(AbstractDistributedApplicationPipeline, result)

    def add_step(self, step_name: str, callback: typing.Callable[[PipelineStepContext], None], *, depends_on: typing.Iterable[str] | None = None, required_by: typing.Iterable[str] | None = None) -> None:
        """Adds a pipeline step to the application"""
        rpc_args: dict[str, typing.Any] = {'pipeline': self._handle}
        rpc_args['stepName'] = step_name
        rpc_args['callback'] = self._client.register_callback(callback)
        if depends_on is not None:
            rpc_args['dependsOn'] = depends_on
        if required_by is not None:
            rpc_args['requiredBy'] = required_by
        self._client.invoke_capability(
            'Aspire.Hosting/addStep',
            rpc_args
        )

    def configure(self, callback: typing.Callable[[PipelineConfigurationContext], None]) -> None:
        """Configures the application pipeline via a callback"""
        rpc_args: dict[str, typing.Any] = {'pipeline': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        self._client.invoke_capability(
            'Aspire.Hosting/configure',
            rpc_args
        )


class AbstractDistributedApplicationResourceEvent(abc.ABC):
    """Abstract base class for AbstractDistributedApplicationResourceEvent."""

class AbstractExecutionConfigurationBuilder:
    """Type class for AbstractExecutionConfigurationBuilder."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractExecutionConfigurationBuilder(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def build(self, execution_context: DistributedApplicationExecutionContext, *, resource_logger: AbstractLogger | None = None, timeout: int | None = None) -> AbstractExecutionConfigurationResult:
        """Builds the execution configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['executionContext'] = execution_context
        if resource_logger is not None:
            rpc_args['resourceLogger'] = resource_logger
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        result = self._client.invoke_capability(
            'Aspire.Hosting/buildExecutionConfiguration',
            rpc_args,
        )
        return typing.cast(AbstractExecutionConfigurationResult, result)

    def with_https_certificate_config(self, config_context_factory: typing.Callable[[HttpsCertificateInfo], HttpsCertificateExecutionConfigurationContext]) -> AbstractExecutionConfigurationBuilder:
        """Adds an HTTPS certificate configuration gatherer"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['configContextFactory'] = self._client.register_callback(config_context_factory)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpsCertificateConfigExport',
            rpc_args,
        )
        return typing.cast(AbstractExecutionConfigurationBuilder, result)

    def with_arguments_config(self) -> AbstractExecutionConfigurationBuilder:
        """Adds an arguments configuration gatherer"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withArgumentsConfig',
            rpc_args,
        )
        return typing.cast(AbstractExecutionConfigurationBuilder, result)

    def with_env_vars_config(self) -> AbstractExecutionConfigurationBuilder:
        """Adds an environment variables configuration gatherer"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEnvironmentVariablesConfig',
            rpc_args,
        )
        return typing.cast(AbstractExecutionConfigurationBuilder, result)

    def with_certificate_trust_config(self, config_context_factory: typing.Callable[[CertificateTrustScope], CertificateTrustExecutionConfigurationContext]) -> AbstractExecutionConfigurationBuilder:
        """Adds a certificate trust configuration gatherer"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['configContextFactory'] = self._client.register_callback(config_context_factory)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withCertificateTrustConfig',
            rpc_args,
        )
        return typing.cast(AbstractExecutionConfigurationBuilder, result)


class AbstractExecutionConfigurationResult:
    """Type class for AbstractExecutionConfigurationResult."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractExecutionConfigurationResult(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def get_certificate_trust_data(self) -> CertificateTrustExecutionConfigurationExportData:
        """Gets certificate trust execution-configuration data"""
        rpc_args: dict[str, typing.Any] = {'configuration': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getCertificateTrustData',
            rpc_args,
        )
        return typing.cast(CertificateTrustExecutionConfigurationExportData, result)

    def get_https_certificate_data(self) -> HttpsCertificateExecutionConfigurationExportData:
        """Gets HTTPS certificate execution-configuration data"""
        rpc_args: dict[str, typing.Any] = {'configuration': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getHttpsCertificateData',
            rpc_args,
        )
        return typing.cast(HttpsCertificateExecutionConfigurationExportData, result)


class AbstractValueProvider(abc.ABC):
    """Abstract base class for AbstractValueProvider."""

class AbstractManifestExpressionProvider(abc.ABC):
    """Abstract base class for AbstractManifestExpressionProvider."""

class AbstractExpressionValue(abc.ABC):
    """Abstract base class for AbstractExpressionValue."""

class AbstractHostEnvironment:
    """Type class for AbstractHostEnvironment."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractHostEnvironment(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def is_development(self) -> bool:
        """Checks if running in Development environment"""
        rpc_args: dict[str, typing.Any] = {'environment': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/isDevelopment',
            rpc_args,
        )
        return result

    def is_production(self) -> bool:
        """Checks if running in Production environment"""
        rpc_args: dict[str, typing.Any] = {'environment': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/isProduction',
            rpc_args,
        )
        return result

    def is_staging(self) -> bool:
        """Checks if running in Staging environment"""
        rpc_args: dict[str, typing.Any] = {'environment': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/isStaging',
            rpc_args,
        )
        return result

    def is_env(self, env_name: str) -> bool:
        """Checks if the environment matches the specified name"""
        rpc_args: dict[str, typing.Any] = {'environment': self._handle}
        rpc_args['environmentName'] = env_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/isEnvironment',
            rpc_args,
        )
        return result


class AbstractLogger:
    """Type class for AbstractLogger."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractLogger(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def log_information(self, message: str) -> None:
        """Logs an information message"""
        rpc_args: dict[str, typing.Any] = {'logger': self._handle}
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting/logInformation',
            rpc_args
        )

    def log_warning(self, message: str) -> None:
        """Logs a warning message"""
        rpc_args: dict[str, typing.Any] = {'logger': self._handle}
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting/logWarning',
            rpc_args
        )

    def log_error(self, message: str) -> None:
        """Logs an error message"""
        rpc_args: dict[str, typing.Any] = {'logger': self._handle}
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting/logError',
            rpc_args
        )

    def log_debug(self, message: str) -> None:
        """Logs a debug message"""
        rpc_args: dict[str, typing.Any] = {'logger': self._handle}
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting/logDebug',
            rpc_args
        )

    def log(self, level: str, message: str) -> None:
        """Logs a message with specified level"""
        rpc_args: dict[str, typing.Any] = {'logger': self._handle}
        rpc_args['level'] = level
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting/log',
            rpc_args
        )


class AbstractLoggerFactory:
    """Type class for AbstractLoggerFactory."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractLoggerFactory(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def create_logger(self, category_name: str) -> AbstractLogger:
        """Creates a logger for a category"""
        rpc_args: dict[str, typing.Any] = {'loggerFactory': self._handle}
        rpc_args['categoryName'] = category_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/createLogger',
            rpc_args,
        )
        return typing.cast(AbstractLogger, result)


class AbstractReportingStep:
    """Type class for AbstractReportingStep."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractReportingStep(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def create_task(self, status_text: str, *, timeout: int | None = None) -> AbstractReportingTask:
        """Creates a reporting task with plain-text status text"""
        rpc_args: dict[str, typing.Any] = {'reportingStep': self._handle}
        rpc_args['statusText'] = status_text
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        result = self._client.invoke_capability(
            'Aspire.Hosting/createTask',
            rpc_args,
        )
        return typing.cast(AbstractReportingTask, result)

    def create_markdown_task(self, markdown_string: str, *, timeout: int | None = None) -> AbstractReportingTask:
        """Creates a reporting task with Markdown-formatted status text"""
        rpc_args: dict[str, typing.Any] = {'reportingStep': self._handle}
        rpc_args['markdownString'] = markdown_string
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        result = self._client.invoke_capability(
            'Aspire.Hosting/createMarkdownTask',
            rpc_args,
        )
        return typing.cast(AbstractReportingTask, result)

    def log_step(self, level: str, message: str) -> None:
        """Logs a plain-text message for the reporting step"""
        rpc_args: dict[str, typing.Any] = {'reportingStep': self._handle}
        rpc_args['level'] = level
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting/logStep',
            rpc_args
        )

    def log_step_markdown(self, level: str, markdown_string: str) -> None:
        """Logs a Markdown-formatted message for the reporting step"""
        rpc_args: dict[str, typing.Any] = {'reportingStep': self._handle}
        rpc_args['level'] = level
        rpc_args['markdownString'] = markdown_string
        self._client.invoke_capability(
            'Aspire.Hosting/logStepMarkdown',
            rpc_args
        )

    def complete_step(self, completion_text: str, *, completion_state: str = "completed", timeout: int | None = None) -> None:
        """Completes the reporting step with plain-text completion text"""
        rpc_args: dict[str, typing.Any] = {'reportingStep': self._handle}
        rpc_args['completionText'] = completion_text
        if completion_state is not None:
            rpc_args['completionState'] = completion_state
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        self._client.invoke_capability(
            'Aspire.Hosting/completeStep',
            rpc_args
        )

    def complete_step_markdown(self, markdown_string: str, *, completion_state: str = "completed", timeout: int | None = None) -> None:
        """Completes the reporting step with Markdown-formatted completion text"""
        rpc_args: dict[str, typing.Any] = {'reportingStep': self._handle}
        rpc_args['markdownString'] = markdown_string
        if completion_state is not None:
            rpc_args['completionState'] = completion_state
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        self._client.invoke_capability(
            'Aspire.Hosting/completeStepMarkdown',
            rpc_args
        )


class AbstractReportingTask:
    """Type class for AbstractReportingTask."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractReportingTask(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def update_task(self, status_text: str, *, timeout: int | None = None) -> None:
        """Updates the reporting task with plain-text status text"""
        rpc_args: dict[str, typing.Any] = {'reportingTask': self._handle}
        rpc_args['statusText'] = status_text
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        self._client.invoke_capability(
            'Aspire.Hosting/updateTask',
            rpc_args
        )

    def update_task_markdown(self, markdown_string: str, *, timeout: int | None = None) -> None:
        """Updates the reporting task with Markdown-formatted status text"""
        rpc_args: dict[str, typing.Any] = {'reportingTask': self._handle}
        rpc_args['markdownString'] = markdown_string
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        self._client.invoke_capability(
            'Aspire.Hosting/updateTaskMarkdown',
            rpc_args
        )

    def complete_task(self, *, completion_message: str | None = None, completion_state: str = "completed", timeout: int | None = None) -> None:
        """Completes the reporting task with plain-text completion text"""
        rpc_args: dict[str, typing.Any] = {'reportingTask': self._handle}
        if completion_message is not None:
            rpc_args['completionMessage'] = completion_message
        if completion_state is not None:
            rpc_args['completionState'] = completion_state
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        self._client.invoke_capability(
            'Aspire.Hosting/completeTask',
            rpc_args
        )

    def complete_task_markdown(self, markdown_string: str, *, completion_state: str = "completed", timeout: int | None = None) -> None:
        """Completes the reporting task with Markdown-formatted completion text"""
        rpc_args: dict[str, typing.Any] = {'reportingTask': self._handle}
        rpc_args['markdownString'] = markdown_string
        if completion_state is not None:
            rpc_args['completionState'] = completion_state
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        self._client.invoke_capability(
            'Aspire.Hosting/completeTaskMarkdown',
            rpc_args
        )


class AbstractValueWithReferences(abc.ABC):
    """Abstract base class for AbstractValueWithReferences."""

class AbstractServiceProvider:
    """Type class for AbstractServiceProvider."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractServiceProvider(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def get_eventing(self) -> AbstractDistributedApplicationEventing:
        """Gets the distributed application eventing service from the service provider"""
        rpc_args: dict[str, typing.Any] = {'serviceProvider': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getEventing',
            rpc_args,
        )
        return typing.cast(AbstractDistributedApplicationEventing, result)

    def get_logger_factory(self) -> AbstractLoggerFactory:
        """Gets the logger factory from the service provider"""
        rpc_args: dict[str, typing.Any] = {'serviceProvider': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getLoggerFactory',
            rpc_args,
        )
        return typing.cast(AbstractLoggerFactory, result)

    def get_resource_logger_service(self) -> ResourceLoggerService:
        """Gets the resource logger service from the service provider"""
        rpc_args: dict[str, typing.Any] = {'serviceProvider': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getResourceLoggerService',
            rpc_args,
        )
        return typing.cast(ResourceLoggerService, result)

    def get_distributed_app_model(self) -> DistributedApplicationModel:
        """Gets the distributed application model from the service provider"""
        rpc_args: dict[str, typing.Any] = {'serviceProvider': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getDistributedApplicationModel',
            rpc_args,
        )
        return typing.cast(DistributedApplicationModel, result)

    def get_resource_notification_service(self) -> ResourceNotificationService:
        """Gets the resource notification service from the service provider"""
        rpc_args: dict[str, typing.Any] = {'serviceProvider': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getResourceNotificationService',
            rpc_args,
        )
        return typing.cast(ResourceNotificationService, result)

    def get_aspire_store(self) -> AbstractAspireStore:
        """Gets the Aspire store from the service provider"""
        rpc_args: dict[str, typing.Any] = {'serviceProvider': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getAspireStore',
            rpc_args,
        )
        return typing.cast(AbstractAspireStore, result)

    def get_user_secrets_manager(self) -> AbstractUserSecretsManager:
        """Gets the user secrets manager from the service provider"""
        rpc_args: dict[str, typing.Any] = {'serviceProvider': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getUserSecretsManager',
            rpc_args,
        )
        return typing.cast(AbstractUserSecretsManager, result)


class AbstractUserSecretsManager:
    """Type class for AbstractUserSecretsManager."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AbstractUserSecretsManager(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def is_available(self) -> bool:
        """Gets the IsAvailable property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/IUserSecretsManager.isAvailable',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def file_path(self) -> str:
        """Gets the FilePath property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/IUserSecretsManager.filePath',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    def try_set_secret(self, name: str, value: str) -> bool:
        """Attempts to set a user secret value"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting/IUserSecretsManager.trySetSecret',
            rpc_args,
        )
        return result

    def try_delete_secret(self, name: str) -> bool:
        """Attempts to delete a user secret value"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/IUserSecretsManager.tryDeleteSecret',
            rpc_args,
        )
        return result

    def save_state_json(self, json: str, *, timeout: int | None = None) -> None:
        """Saves state to user secrets from a JSON string"""
        rpc_args: dict[str, typing.Any] = {'userSecretsManager': self._handle}
        rpc_args['json'] = json
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        self._client.invoke_capability(
            'Aspire.Hosting/saveStateJson',
            rpc_args
        )

    def get_or_set_secret(self, resource_builder: AbstractResource, name: str, value: str) -> None:
        """Gets a secret value if it exists, or sets it to the provided value if it does not"""
        rpc_args: dict[str, typing.Any] = {'userSecretsManager': self._handle}
        rpc_args['resourceBuilder'] = resource_builder
        rpc_args['name'] = name
        rpc_args['value'] = value
        self._client.invoke_capability(
            'Aspire.Hosting/getOrSetSecret',
            rpc_args
        )


class AfterResourcesCreatedEvent:
    """Type class for AfterResourcesCreatedEvent."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"AfterResourcesCreatedEvent(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)

    @_cached_property
    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.model',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationModel, result)


class BeforeResourceStartedEvent:
    """Type class for BeforeResourceStartedEvent."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"BeforeResourceStartedEvent(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)


class BeforeStartEvent:
    """Type class for BeforeStartEvent."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"BeforeStartEvent(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/BeforeStartEvent.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)

    @_cached_property
    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/BeforeStartEvent.model',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationModel, result)


class CommandLineArgsCallbackContext:
    """Type class for CommandLineArgsCallbackContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"CommandLineArgsCallbackContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def args(self) -> CommandLineArgsEditor:
        """Gets the command-line argument editor"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.args',
            {'context': self._handle}
        )
        return typing.cast(CommandLineArgsEditor, result)

    @_cached_property
    def log(self) -> LogFacade:
        """Gets the callback logger facade"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.log',
            {'context': self._handle}
        )
        return typing.cast(LogFacade, result)

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the resource associated with this callback"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the execution context for this callback invocation"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.executionContext',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationExecutionContext, result)


class CommandLineArgsEditor:
    """Type class for CommandLineArgsEditor."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"CommandLineArgsEditor(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def add(self, value: str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue) -> None:
        """Adds a command-line argument"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['value'] = value
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/add',
            rpc_args
        )


class ConnectionStringAvailableEvent:
    """Type class for ConnectionStringAvailableEvent."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ConnectionStringAvailableEvent(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)


class ContainerImagePushOptions:
    """Type class for ContainerImagePushOptions."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ContainerImagePushOptions(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def remote_image_name(self) -> str:
        """Gets the RemoteImageName property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.remoteImageName',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @remote_image_name.setter
    def remote_image_name(self, value: str) -> None:
        """Sets the RemoteImageName property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.setRemoteImageName',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def remote_image_tag(self) -> str:
        """Gets the RemoteImageTag property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.remoteImageTag',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @remote_image_tag.setter
    def remote_image_tag(self, value: str) -> None:
        """Sets the RemoteImageTag property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.setRemoteImageTag',
            {'context': self._handle, 'value': value}
        )


class ContainerImagePushOptionsCallbackContext:
    """Type class for ContainerImagePushOptionsCallbackContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ContainerImagePushOptionsCallbackContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @resource.setter
    def resource(self, value: AbstractResource) -> None:
        """Sets the Resource property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.setResource',
            {'context': self._handle, 'value': value}
        )

    def cancel(self) -> None:
        """Cancel the operation."""
        token: CancellationToken = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.cancellationToken',
            {'context': self._handle}
        )
        token.cancel()

    @_uncached_property
    def options(self) -> ContainerImagePushOptions:
        """Gets the Options property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.options',
            {'context': self._handle}
        )
        return typing.cast(ContainerImagePushOptions, result)

    @options.setter
    def options(self, value: ContainerImagePushOptions) -> None:
        """Sets the Options property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.setOptions',
            {'context': self._handle, 'value': value}
        )


class DistributedApplication:
    """Type class for DistributedApplication."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"DistributedApplication(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def run(self, *, timeout: int | None = None) -> None:
        """Runs the distributed application"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        self._client.invoke_capability(
            'Aspire.Hosting/run',
            rpc_args
        )


class DistributedApplicationEventSubscription:
    """Type class for DistributedApplicationEventSubscription."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"DistributedApplicationEventSubscription(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle


class DistributedApplicationExecutionContext:
    """Type class for DistributedApplicationExecutionContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"DistributedApplicationExecutionContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def publisher_name(self) -> str:
        """Gets the PublisherName property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/DistributedApplicationExecutionContext.publisherName',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @publisher_name.setter
    def publisher_name(self, value: str) -> None:
        """Sets the PublisherName property"""
        self._client.invoke_capability(
            'Aspire.Hosting/DistributedApplicationExecutionContext.setPublisherName',
            {'context': self._handle, 'value': value}
        )

    @_cached_property
    def operation(self) -> DistributedApplicationOperation:
        """Gets the Operation property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/DistributedApplicationExecutionContext.operation',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationOperation, result)

    @_cached_property
    def service_provider(self) -> AbstractServiceProvider:
        """Gets the ServiceProvider property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/DistributedApplicationExecutionContext.serviceProvider',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)

    @_cached_property
    def is_publish_mode(self) -> bool:
        """Gets the IsPublishMode property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/DistributedApplicationExecutionContext.isPublishMode',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def is_run_mode(self) -> bool:
        """Gets the IsRunMode property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/DistributedApplicationExecutionContext.isRunMode',
            {'context': self._handle}
        )
        return typing.cast(bool, result)


class DistributedApplicationModel:
    """Type class for DistributedApplicationModel."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"DistributedApplicationModel(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def get_resources(self) -> typing.Iterable[AbstractResource]:
        """Gets resources from the distributed application model"""
        rpc_args: dict[str, typing.Any] = {'model': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getResources',
            rpc_args,
        )
        return result

    def find_resource_by_name(self, name: str) -> AbstractResource:
        """Finds a resource by name"""
        rpc_args: dict[str, typing.Any] = {'model': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/findResourceByName',
            rpc_args,
        )
        return typing.cast(AbstractResource, result)


class DockerfileBuilder:
    """Type class for DockerfileBuilder."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"DockerfileBuilder(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def arg(self, name: str, *, default_value: str | None = None) -> DockerfileBuilder:
        """Adds a global ARG statement to the Dockerfile"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        if default_value is not None:
            rpc_args['defaultValue'] = default_value
        result = self._client.invoke_capability(
            'Aspire.Hosting/dockerfileBuilderArg',
            rpc_args,
        )
        return typing.cast(DockerfileBuilder, result)

    def from_(self, image: str, *, stage_name: str | None = None) -> DockerfileStage:
        """Adds a FROM statement to start a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['image'] = image
        if stage_name is not None:
            rpc_args['stageName'] = stage_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/dockerfileBuilderFrom',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def add_container_files_stages(self, resource: AbstractResource, *, logger: AbstractLogger | None = None) -> DockerfileBuilder:
        """Adds Dockerfile stages for published container files"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['resource'] = resource
        if logger is not None:
            rpc_args['logger'] = logger
        result = self._client.invoke_capability(
            'Aspire.Hosting/dockerfileBuilderAddContainerFilesStages',
            rpc_args,
        )
        return typing.cast(DockerfileBuilder, result)


class DockerfileBuilderCallbackContext:
    """Type class for DockerfileBuilderCallbackContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"DockerfileBuilderCallbackContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def builder(self) -> DockerfileBuilder:
        """Gets the Builder property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.builder',
            {'context': self._handle}
        )
        return typing.cast(DockerfileBuilder, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)

    def cancel(self) -> None:
        """Cancel the operation."""
        token: CancellationToken = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.cancellationToken',
            {'context': self._handle}
        )
        token.cancel()


class DockerfileStage:
    """Type class for DockerfileStage."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"DockerfileStage(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def arg(self, name: str, *, default_value: str | None = None) -> DockerfileStage:
        """Adds an ARG statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['name'] = name
        if default_value is not None:
            rpc_args['defaultValue'] = default_value
        result = self._client.invoke_capability(
            'Aspire.Hosting/dockerfileStageArg',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def work_dir(self, path: str) -> DockerfileStage:
        """Adds a WORKDIR statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['path'] = path
        result = self._client.invoke_capability(
            'Aspire.Hosting/workDir',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def run(self, command: str) -> DockerfileStage:
        """Adds a RUN statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['command'] = command
        result = self._client.invoke_capability(
            'Aspire.Hosting/dockerfileStageRun',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def copy(self, source: str, destination: str, *, chown: str | None = None) -> DockerfileStage:
        """Adds a COPY statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['source'] = source
        rpc_args['destination'] = destination
        if chown is not None:
            rpc_args['chown'] = chown
        result = self._client.invoke_capability(
            'Aspire.Hosting/dockerfileStageCopy',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def copy_from(self, from_: str, source: str, destination: str, *, chown: str | None = None) -> DockerfileStage:
        """Adds a COPY --from statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['from'] = from_
        rpc_args['source'] = source
        rpc_args['destination'] = destination
        if chown is not None:
            rpc_args['chown'] = chown
        result = self._client.invoke_capability(
            'Aspire.Hosting/dockerfileStageCopyFrom',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def env(self, name: str, value: str) -> DockerfileStage:
        """Adds an ENV statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting/env',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def expose(self, port: int) -> DockerfileStage:
        """Adds an EXPOSE statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['port'] = port
        result = self._client.invoke_capability(
            'Aspire.Hosting/expose',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def cmd(self, command: typing.Iterable[str]) -> DockerfileStage:
        """Adds a CMD statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['command'] = command
        result = self._client.invoke_capability(
            'Aspire.Hosting/cmd',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def entrypoint(self, command: typing.Iterable[str]) -> DockerfileStage:
        """Adds an ENTRYPOINT statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['command'] = command
        result = self._client.invoke_capability(
            'Aspire.Hosting/entrypoint',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def run_with_mounts(self, command: str, mounts: typing.Iterable[str]) -> DockerfileStage:
        """Adds a RUN statement with mounts to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['command'] = command
        rpc_args['mounts'] = mounts
        result = self._client.invoke_capability(
            'Aspire.Hosting/runWithMounts',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def user(self, user: str) -> DockerfileStage:
        """Adds a USER statement to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['user'] = user
        result = self._client.invoke_capability(
            'Aspire.Hosting/user',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def empty_line(self) -> DockerfileStage:
        """Adds an empty line to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/emptyLine',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def comment(self, comment: str) -> DockerfileStage:
        """Adds a comment to a Dockerfile stage"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['comment'] = comment
        result = self._client.invoke_capability(
            'Aspire.Hosting/comment',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)

    def add_container_files(self, resource: AbstractResource, root_destination_path: str, *, logger: AbstractLogger | None = None) -> DockerfileStage:
        """Adds COPY --from statements for published container files"""
        rpc_args: dict[str, typing.Any] = {'stage': self._handle}
        rpc_args['resource'] = resource
        rpc_args['rootDestinationPath'] = root_destination_path
        if logger is not None:
            rpc_args['logger'] = logger
        result = self._client.invoke_capability(
            'Aspire.Hosting/dockerfileStageAddContainerFiles',
            rpc_args,
        )
        return typing.cast(DockerfileStage, result)


class EndpointReference:
    """Type class for EndpointReference."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"EndpointReference(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResourceWithEndpoints:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResourceWithEndpoints, result)

    @_cached_property
    def endpoint_name(self) -> str:
        """Gets the EndpointName property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.endpointName',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @_uncached_property
    def error_message(self) -> str:
        """Gets the ErrorMessage property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.errorMessage',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @error_message.setter
    def error_message(self, value: str) -> None:
        """Sets the ErrorMessage property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.setErrorMessage',
            {'context': self._handle, 'value': value}
        )

    @_cached_property
    def is_allocated(self) -> bool:
        """Gets the IsAllocated property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.isAllocated',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def exists(self) -> bool:
        """Gets the Exists property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.exists',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def is_http(self) -> bool:
        """Gets the IsHttp property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.isHttp',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def is_https(self) -> bool:
        """Gets the IsHttps property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.isHttps',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def tls_enabled(self) -> bool:
        """Gets the TlsEnabled property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.tlsEnabled',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def is_http_scheme_named_endpoint(self) -> bool:
        """Gets the IsHttpSchemeNamedEndpoint property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.isHttpSchemeNamedEndpoint',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def exclude_reference_endpoint(self) -> bool:
        """Gets the ExcludeReferenceEndpoint property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.excludeReferenceEndpoint',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @_cached_property
    def port(self) -> int:
        """Gets the Port property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.port',
            {'context': self._handle}
        )
        return typing.cast(int, result)

    @_cached_property
    def target_port(self) -> int:
        """Gets the TargetPort property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.targetPort',
            {'context': self._handle}
        )
        return typing.cast(int, result)

    @_cached_property
    def host(self) -> str:
        """Gets the Host property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.host',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @_cached_property
    def scheme(self) -> str:
        """Gets the Scheme property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.scheme',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @_cached_property
    def url(self) -> str:
        """Gets the Url property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.url',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    def get_value(self, *, timeout: int | None = None) -> str:
        """Gets the URL of the endpoint asynchronously"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        if timeout is not None:
            rpc_args['cancellationToken'] = self._client.register_cancellation_token(timeout)
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.getValueAsync',
            rpc_args,
        )
        return result

    def property(self, property: EndpointProperty) -> EndpointReferenceExpression:
        """Gets the specified property expression of the endpoint"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['property'] = property
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.property',
            rpc_args,
        )
        return typing.cast(EndpointReferenceExpression, result)

    def get_tls_value(self, enabled_value: ReferenceExpression, disabled_value: ReferenceExpression) -> ReferenceExpression:
        """Gets a conditional expression that resolves to the enabledValue when TLS is enabled on the endpoint, or to the disabledValue otherwise."""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['enabledValue'] = enabled_value
        rpc_args['disabledValue'] = disabled_value
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReference.getTlsValue',
            rpc_args,
        )
        return typing.cast(ReferenceExpression, result)


class EndpointReferenceExpression:
    """Type class for EndpointReferenceExpression."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"EndpointReferenceExpression(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def endpoint(self) -> EndpointReference:
        """Gets the Endpoint property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.endpoint',
            {'context': self._handle}
        )
        return typing.cast(EndpointReference, result)

    @_cached_property
    def property(self) -> EndpointProperty:
        """Gets the Property property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.property',
            {'context': self._handle}
        )
        return typing.cast(EndpointProperty, result)

    @_cached_property
    def value_expression(self) -> str:
        """Gets the ValueExpression property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.valueExpression',
            {'context': self._handle}
        )
        return typing.cast(str, result)


class EndpointUpdateContext:
    """Type class for EndpointUpdateContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"EndpointUpdateContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def name(self) -> str:
        """Gets the Name property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.name',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @_uncached_property
    def protocol(self) -> ProtocolType:
        """Gets the Protocol property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.protocol',
            {'context': self._handle}
        )
        return typing.cast(ProtocolType, result)

    @protocol.setter
    def protocol(self, value: ProtocolType) -> None:
        """Sets the Protocol property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setProtocol',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def port(self) -> int:
        """Gets the Port property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.port',
            {'context': self._handle}
        )
        return typing.cast(int, result)

    @port.setter
    def port(self, value: int) -> None:
        """Sets the Port property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setPort',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def target_port(self) -> int:
        """Gets the TargetPort property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.targetPort',
            {'context': self._handle}
        )
        return typing.cast(int, result)

    @target_port.setter
    def target_port(self, value: int) -> None:
        """Sets the TargetPort property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTargetPort',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def uri_scheme(self) -> str:
        """Gets the UriScheme property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.uriScheme',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @uri_scheme.setter
    def uri_scheme(self, value: str) -> None:
        """Sets the UriScheme property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setUriScheme',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def target_host(self) -> str:
        """Gets the TargetHost property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.targetHost',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @target_host.setter
    def target_host(self, value: str) -> None:
        """Sets the TargetHost property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTargetHost',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def transport(self) -> str:
        """Gets the Transport property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.transport',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @transport.setter
    def transport(self, value: str) -> None:
        """Sets the Transport property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTransport',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def is_external(self) -> bool:
        """Gets the IsExternal property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.isExternal',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @is_external.setter
    def is_external(self, value: bool) -> None:
        """Sets the IsExternal property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setIsExternal',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def is_proxied(self) -> bool:
        """Gets the IsProxied property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.isProxied',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @is_proxied.setter
    def is_proxied(self, value: bool) -> None:
        """Sets the IsProxied property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setIsProxied',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def exclude_reference_endpoint(self) -> bool:
        """Gets the ExcludeReferenceEndpoint property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.excludeReferenceEndpoint',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @exclude_reference_endpoint.setter
    def exclude_reference_endpoint(self, value: bool) -> None:
        """Sets the ExcludeReferenceEndpoint property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setExcludeReferenceEndpoint',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def tls_enabled(self) -> bool:
        """Gets the TlsEnabled property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.tlsEnabled',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @tls_enabled.setter
    def tls_enabled(self, value: bool) -> None:
        """Sets the TlsEnabled property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTlsEnabled',
            {'context': self._handle, 'value': value}
        )


class EnvironmentCallbackContext:
    """Type class for EnvironmentCallbackContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"EnvironmentCallbackContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def env(self) -> EnvironmentEditor:
        """Gets the environment variable editor"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environment',
            {'context': self._handle}
        )
        return typing.cast(EnvironmentEditor, result)

    @_cached_property
    def log(self) -> LogFacade:
        """Gets the callback logger facade"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.log',
            {'context': self._handle}
        )
        return typing.cast(LogFacade, result)

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the resource associated with this callback"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the execution context for this callback invocation"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.executionContext',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationExecutionContext, result)


class EnvironmentEditor:
    """Type class for EnvironmentEditor."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"EnvironmentEditor(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def set(self, name: str, value: str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue) -> None:
        """Sets an environment variable"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/set',
            rpc_args
        )


class EventingSubscriberRegistrationContext:
    """Type class for EventingSubscriberRegistrationContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"EventingSubscriberRegistrationContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Ats/EventingSubscriberRegistrationContext.executionContext',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationExecutionContext, result)

    def cancel(self) -> None:
        """Cancel the operation."""
        token: CancellationToken = self._client.invoke_capability(
            'Aspire.Hosting.Ats/EventingSubscriberRegistrationContext.cancellationToken',
            {'context': self._handle}
        )
        token.cancel()

    def on_before_start(self, callback: typing.Callable[[BeforeStartEvent], None]) -> DistributedApplicationEventSubscription:
        """Subscribes an eventing subscriber to the BeforeStart event"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/eventingSubscriberOnBeforeStart',
            rpc_args,
        )
        return typing.cast(DistributedApplicationEventSubscription, result)

    def on_after_resources_created(self, callback: typing.Callable[[AfterResourcesCreatedEvent], None]) -> DistributedApplicationEventSubscription:
        """Subscribes an eventing subscriber to the AfterResourcesCreated event"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/eventingSubscriberOnAfterResourcesCreated',
            rpc_args,
        )
        return typing.cast(DistributedApplicationEventSubscription, result)


class ExecuteCommandContext:
    """Type class for ExecuteCommandContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ExecuteCommandContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def service_provider(self) -> AbstractServiceProvider:
        """Gets the ServiceProvider property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ExecuteCommandContext.serviceProvider',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)

    @service_provider.setter
    def service_provider(self, value: AbstractServiceProvider) -> None:
        """Sets the ServiceProvider property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setServiceProvider',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def resource_name(self) -> str:
        """Gets the ResourceName property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ExecuteCommandContext.resourceName',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @resource_name.setter
    def resource_name(self, value: str) -> None:
        """Sets the ResourceName property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setResourceName',
            {'context': self._handle, 'value': value}
        )

    def cancel(self) -> None:
        """Cancel the operation."""
        token: CancellationToken = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ExecuteCommandContext.cancellationToken',
            {'context': self._handle}
        )
        token.cancel()

    @_uncached_property
    def logger(self) -> AbstractLogger:
        """Gets the Logger property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ExecuteCommandContext.logger',
            {'context': self._handle}
        )
        return typing.cast(AbstractLogger, result)

    @logger.setter
    def logger(self, value: AbstractLogger) -> None:
        """Sets the Logger property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setLogger',
            {'context': self._handle, 'value': value}
        )


class InitializeResourceEvent:
    """Type class for InitializeResourceEvent."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"InitializeResourceEvent(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/InitializeResourceEvent.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def eventing(self) -> AbstractDistributedApplicationEventing:
        """Gets the Eventing property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/InitializeResourceEvent.eventing',
            {'context': self._handle}
        )
        return typing.cast(AbstractDistributedApplicationEventing, result)

    @_cached_property
    def logger(self) -> AbstractLogger:
        """Gets the Logger property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/InitializeResourceEvent.logger',
            {'context': self._handle}
        )
        return typing.cast(AbstractLogger, result)

    @_cached_property
    def notifications(self) -> ResourceNotificationService:
        """Gets the Notifications property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/InitializeResourceEvent.notifications',
            {'context': self._handle}
        )
        return typing.cast(ResourceNotificationService, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/InitializeResourceEvent.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)


class LogFacade:
    """Type class for LogFacade."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"LogFacade(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def info(self, message: str) -> None:
        """Writes an informational log message"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/info',
            rpc_args
        )

    def warning(self, message: str) -> None:
        """Writes a warning log message"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/warning',
            rpc_args
        )

    def error(self, message: str) -> None:
        """Writes an error log message"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/error',
            rpc_args
        )

    def debug(self, message: str) -> None:
        """Writes a debug log message"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['message'] = message
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/debug',
            rpc_args
        )


class PipelineConfigurationContext:
    """Type class for PipelineConfigurationContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"PipelineConfigurationContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def pipeline(self) -> PipelineEditor:
        """Gets the pipeline editor"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineConfigurationContext.pipeline',
            {'context': self._handle}
        )
        return typing.cast(PipelineEditor, result)

    @_cached_property
    def log(self) -> LogFacade:
        """Gets the callback logger facade"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineConfigurationContext.log',
            {'context': self._handle}
        )
        return typing.cast(LogFacade, result)

    def get_steps(self, tag: str) -> typing.Iterable[PipelineStep]:
        """Gets pipeline steps with the specified tag"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['tag'] = tag
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/getSteps',
            rpc_args,
        )
        return result


class PipelineContext:
    """Type class for PipelineContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"PipelineContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineContext.model',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationModel, result)

    @_cached_property
    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineContext.executionContext',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationExecutionContext, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineContext.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)

    @_cached_property
    def logger(self) -> AbstractLogger:
        """Gets the Logger property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineContext.logger',
            {'context': self._handle}
        )
        return typing.cast(AbstractLogger, result)

    def cancel(self) -> None:
        """Cancel the operation."""
        token: CancellationToken = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineContext.cancellationToken',
            {'context': self._handle}
        )
        token.cancel()

    @_cached_property
    def summary(self) -> PipelineSummary:
        """Gets the Summary property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineContext.summary',
            {'context': self._handle}
        )
        return typing.cast(PipelineSummary, result)


class PipelineEditor:
    """Type class for PipelineEditor."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"PipelineEditor(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def steps(self) -> typing.Iterable[PipelineStep]:
        """Gets all configured pipeline steps"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/steps',
            rpc_args,
        )
        return result

    def steps_by_tag(self, tag: str) -> typing.Iterable[PipelineStep]:
        """Gets pipeline steps with the specified tag"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['tag'] = tag
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/stepsByTag',
            rpc_args,
        )
        return result


class PipelineStep:
    """Type class for PipelineStep."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"PipelineStep(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def name(self) -> str:
        """Gets the unique name of the step"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStep.name',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @_cached_property
    def description(self) -> str:
        """Gets the human-readable description of the step"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStep.description',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    def depends_on(self, step_name: str) -> None:
        """Adds a dependency on another step by name"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['stepName'] = step_name
        self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/dependsOn',
            rpc_args
        )

    def required_by(self, step_name: str) -> None:
        """Specifies that another step requires this step by name"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['stepName'] = step_name
        self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/requiredBy',
            rpc_args
        )

    def add_tag(self, tag: str) -> None:
        """Adds a tag to the step"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['tag'] = tag
        self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/addTag',
            rpc_args
        )


class PipelineStepContext:
    """Type class for PipelineStepContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"PipelineStepContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def pipeline_context(self) -> PipelineContext:
        """Gets the PipelineContext property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.pipelineContext',
            {'context': self._handle}
        )
        return typing.cast(PipelineContext, result)

    @pipeline_context.setter
    def pipeline_context(self, value: PipelineContext) -> None:
        """Sets the PipelineContext property"""
        self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.setPipelineContext',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def reporting_step(self) -> AbstractReportingStep:
        """Gets the ReportingStep property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.reportingStep',
            {'context': self._handle}
        )
        return typing.cast(AbstractReportingStep, result)

    @reporting_step.setter
    def reporting_step(self, value: AbstractReportingStep) -> None:
        """Sets the ReportingStep property"""
        self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.setReportingStep',
            {'context': self._handle, 'value': value}
        )

    @_cached_property
    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.model',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationModel, result)

    @_cached_property
    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.executionContext',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationExecutionContext, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)

    @_cached_property
    def logger(self) -> AbstractLogger:
        """Gets the Logger property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.logger',
            {'context': self._handle}
        )
        return typing.cast(AbstractLogger, result)

    def cancel(self) -> None:
        """Cancel the operation."""
        token: CancellationToken = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.cancellationToken',
            {'context': self._handle}
        )
        token.cancel()

    @_cached_property
    def summary(self) -> PipelineSummary:
        """Gets the Summary property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepContext.summary',
            {'context': self._handle}
        )
        return typing.cast(PipelineSummary, result)


class PipelineStepFactoryContext:
    """Type class for PipelineStepFactoryContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"PipelineStepFactoryContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def pipeline_context(self) -> PipelineContext:
        """Gets the PipelineContext property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepFactoryContext.pipelineContext',
            {'context': self._handle}
        )
        return typing.cast(PipelineContext, result)

    @pipeline_context.setter
    def pipeline_context(self, value: PipelineContext) -> None:
        """Sets the PipelineContext property"""
        self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setPipelineContext',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepFactoryContext.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @resource.setter
    def resource(self, value: AbstractResource) -> None:
        """Sets the Resource property"""
        self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setResource',
            {'context': self._handle, 'value': value}
        )


class PipelineSummary:
    """Type class for PipelineSummary."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"PipelineSummary(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def add(self, key: str, value: str) -> None:
        """Invokes the Add method"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['key'] = key
        rpc_args['value'] = value
        self._client.invoke_capability(
            'Aspire.Hosting.Pipelines/PipelineSummary.add',
            rpc_args
        )

    def add_markdown(self, key: str, markdown_string: str) -> None:
        """Adds a Markdown-formatted value to the pipeline summary"""
        rpc_args: dict[str, typing.Any] = {'summary': self._handle}
        rpc_args['key'] = key
        rpc_args['markdownString'] = markdown_string
        self._client.invoke_capability(
            'Aspire.Hosting/addMarkdown',
            rpc_args
        )


class ProjectResourceOptions:
    """Type class for ProjectResourceOptions."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ProjectResourceOptions(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def launch_profile_name(self) -> str:
        """Gets the LaunchProfileName property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/ProjectResourceOptions.launchProfileName',
            {'context': self._handle}
        )
        return typing.cast(str, result)

    @launch_profile_name.setter
    def launch_profile_name(self, value: str) -> None:
        """Sets the LaunchProfileName property"""
        self._client.invoke_capability(
            'Aspire.Hosting/ProjectResourceOptions.setLaunchProfileName',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def exclude_launch_profile(self) -> bool:
        """Gets the ExcludeLaunchProfile property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/ProjectResourceOptions.excludeLaunchProfile',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @exclude_launch_profile.setter
    def exclude_launch_profile(self, value: bool) -> None:
        """Sets the ExcludeLaunchProfile property"""
        self._client.invoke_capability(
            'Aspire.Hosting/ProjectResourceOptions.setExcludeLaunchProfile',
            {'context': self._handle, 'value': value}
        )

    @_uncached_property
    def exclude_kestrel_endpoints(self) -> bool:
        """Gets the ExcludeKestrelEndpoints property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting/ProjectResourceOptions.excludeKestrelEndpoints',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    @exclude_kestrel_endpoints.setter
    def exclude_kestrel_endpoints(self, value: bool) -> None:
        """Sets the ExcludeKestrelEndpoints property"""
        self._client.invoke_capability(
            'Aspire.Hosting/ProjectResourceOptions.setExcludeKestrelEndpoints',
            {'context': self._handle, 'value': value}
        )


class ReferenceExpressionBuilder:
    """Type class for ReferenceExpressionBuilder."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ReferenceExpressionBuilder(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def is_empty(self) -> bool:
        """Gets the IsEmpty property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ReferenceExpressionBuilder.isEmpty',
            {'context': self._handle}
        )
        return typing.cast(bool, result)

    def append_literal(self, value: str) -> None:
        """Appends a literal string to the reference expression"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['value'] = value
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/appendLiteral',
            rpc_args
        )

    def append_formatted(self, value: str, *, format: str | None = None) -> None:
        """Appends a formatted string value to the reference expression"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['value'] = value
        if format is not None:
            rpc_args['format'] = format
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/appendFormatted',
            rpc_args
        )

    def append_value_provider(self, value_provider: typing.Any, *, format: str | None = None) -> None:
        """Appends a value provider to the reference expression"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['valueProvider'] = value_provider
        if format is not None:
            rpc_args['format'] = format
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/appendValueProvider',
            rpc_args
        )

    def build(self) -> ReferenceExpression:
        """Builds the reference expression"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/build',
            rpc_args,
        )
        return typing.cast(ReferenceExpression, result)


class ResourceEndpointsAllocatedEvent:
    """Type class for ResourceEndpointsAllocatedEvent."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ResourceEndpointsAllocatedEvent(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)


class ResourceLoggerService:
    """Type class for ResourceLoggerService."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ResourceLoggerService(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def complete_log(self, resource: AbstractResource) -> None:
        """Completes the log stream for a resource"""
        rpc_args: dict[str, typing.Any] = {'loggerService': self._handle}
        rpc_args['resource'] = resource
        self._client.invoke_capability(
            'Aspire.Hosting/completeLog',
            rpc_args
        )

    def complete_log_by_name(self, resource_name: str) -> None:
        """Completes the log stream by resource name"""
        rpc_args: dict[str, typing.Any] = {'loggerService': self._handle}
        rpc_args['resourceName'] = resource_name
        self._client.invoke_capability(
            'Aspire.Hosting/completeLogByName',
            rpc_args
        )


class ResourceNotificationService:
    """Type class for ResourceNotificationService."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ResourceNotificationService(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def wait_for_resource_state(self, resource_name: str, *, target_state: str | None = None) -> None:
        """Waits for a resource to reach a specified state"""
        rpc_args: dict[str, typing.Any] = {'notificationService': self._handle}
        rpc_args['resourceName'] = resource_name
        if target_state is not None:
            rpc_args['targetState'] = target_state
        self._client.invoke_capability(
            'Aspire.Hosting/waitForResourceState',
            rpc_args
        )

    def wait_for_resource_states(self, resource_name: str, target_states: typing.Iterable[str]) -> str:
        """Waits for a resource to reach one of the specified states"""
        rpc_args: dict[str, typing.Any] = {'notificationService': self._handle}
        rpc_args['resourceName'] = resource_name
        rpc_args['targetStates'] = target_states
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitForResourceStates',
            rpc_args,
        )
        return result

    def wait_for_resource_healthy(self, resource_name: str) -> ResourceEventDto:
        """Waits for a resource to become healthy"""
        rpc_args: dict[str, typing.Any] = {'notificationService': self._handle}
        rpc_args['resourceName'] = resource_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitForResourceHealthy',
            rpc_args,
        )
        return typing.cast(ResourceEventDto, result)

    def wait_for_dependencies(self, resource: AbstractResource) -> None:
        """Waits for all dependencies of a resource to be ready"""
        rpc_args: dict[str, typing.Any] = {'notificationService': self._handle}
        rpc_args['resource'] = resource
        self._client.invoke_capability(
            'Aspire.Hosting/waitForDependencies',
            rpc_args
        )

    def try_get_resource_state(self, resource_name: str) -> ResourceEventDto:
        """Tries to get the current state of a resource"""
        rpc_args: dict[str, typing.Any] = {'notificationService': self._handle}
        rpc_args['resourceName'] = resource_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/tryGetResourceState',
            rpc_args,
        )
        return typing.cast(ResourceEventDto, result)

    def publish_resource_update(self, resource: AbstractResource, *, state: str | None = None, state_style: str | None = None) -> None:
        """Publishes an update for a resource's state"""
        rpc_args: dict[str, typing.Any] = {'notificationService': self._handle}
        rpc_args['resource'] = resource
        if state is not None:
            rpc_args['state'] = state
        if state_style is not None:
            rpc_args['stateStyle'] = state_style
        self._client.invoke_capability(
            'Aspire.Hosting/publishResourceUpdate',
            rpc_args
        )


class ResourceReadyEvent:
    """Type class for ResourceReadyEvent."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ResourceReadyEvent(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceReadyEvent.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceReadyEvent.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)


class ResourceStoppedEvent:
    """Type class for ResourceStoppedEvent."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ResourceStoppedEvent(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the Resource property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def services(self) -> AbstractServiceProvider:
        """Gets the Services property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.services',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)


class ResourceUrlsCallbackContext:
    """Type class for ResourceUrlsCallbackContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ResourceUrlsCallbackContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def resource(self) -> AbstractResource:
        """Gets the resource associated with these URLs"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.resource',
            {'context': self._handle}
        )
        return typing.cast(AbstractResource, result)

    @_cached_property
    def urls(self) -> ResourceUrlsEditor:
        """Gets the URL editor"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.urls',
            {'context': self._handle}
        )
        return typing.cast(ResourceUrlsEditor, result)

    @_cached_property
    def log(self) -> LogFacade:
        """Gets the callback logger facade"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.log',
            {'context': self._handle}
        )
        return typing.cast(LogFacade, result)

    @_cached_property
    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the execution context for this callback invocation"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.executionContext',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationExecutionContext, result)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference from the associated resource"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/getEndpoint',
            rpc_args,
        )
        return typing.cast(EndpointReference, result)


class ResourceUrlsEditor:
    """Type class for ResourceUrlsEditor."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"ResourceUrlsEditor(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_cached_property
    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the execution context for this URL editor"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.executionContext',
            {'context': self._handle}
        )
        return typing.cast(DistributedApplicationExecutionContext, result)

    def add(self, url: str | ReferenceExpression, *, display_text: str | None = None) -> None:
        """Adds a displayed URL"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['url'] = url
        if display_text is not None:
            rpc_args['displayText'] = display_text
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.add',
            rpc_args
        )

    def add_for_endpoint(self, endpoint: EndpointReference, url: str | ReferenceExpression, *, display_text: str | None = None) -> None:
        """Adds a displayed URL for a specific endpoint"""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['endpoint'] = endpoint
        rpc_args['url'] = url
        if display_text is not None:
            rpc_args['displayText'] = display_text
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.addForEndpoint',
            rpc_args
        )


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


class UpdateCommandStateContext:
    """Type class for UpdateCommandStateContext."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"UpdateCommandStateContext(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    @_uncached_property
    def service_provider(self) -> AbstractServiceProvider:
        """Gets the ServiceProvider property"""
        result = self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.serviceProvider',
            {'context': self._handle}
        )
        return typing.cast(AbstractServiceProvider, result)

    @service_provider.setter
    def service_provider(self, value: AbstractServiceProvider) -> None:
        """Sets the ServiceProvider property"""
        self._client.invoke_capability(
            'Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.setServiceProvider',
            {'context': self._handle, 'value': value}
        )


# ============================================================================
# Interface Classes
# ============================================================================

class AbstractResource(abc.ABC):
    """Abstract base class for AbstractResource interface."""

    @abc.abstractmethod
    def with_container_registry(self, registry: AbstractResource) -> typing.Self:
        """Configures a resource to use a container registry"""

    @abc.abstractmethod
    def with_dockerfile_base_image(self, *, build_image: str | None = None, runtime_image: str | None = None) -> typing.Self:
        """Sets the base image for a Dockerfile build"""

    @abc.abstractmethod
    def with_required_command(self, command: str, *, help_link: str | None = None) -> typing.Self:
        """Adds a required command dependency"""

    @abc.abstractmethod
    def with_urls(self, callback: typing.Callable[[ResourceUrlsCallbackContext], None]) -> typing.Self:
        """Customizes displayed URLs via callback"""

    @abc.abstractmethod
    def with_url(self, url: str | ReferenceExpression, *, display_text: str | None = None) -> typing.Self:
        """Adds or modifies displayed URLs"""

    @abc.abstractmethod
    def with_url_for_endpoint(self, endpoint_name: str, callback: typing.Callable[[ResourceUrlAnnotation], None]) -> typing.Self:
        """Customizes the URL for a specific endpoint via callback"""

    @abc.abstractmethod
    def exclude_from_manifest(self) -> typing.Self:
        """Excludes the resource from the deployment manifest"""

    @abc.abstractmethod
    def with_explicit_start(self) -> typing.Self:
        """Prevents resource from starting automatically"""

    @abc.abstractmethod
    def with_health_check(self, key: str) -> typing.Self:
        """Adds a health check by key"""

    @abc.abstractmethod
    def with_command(self, name: str, display_name: str, execute_command: typing.Callable[[ExecuteCommandContext], ExecuteCommandResult], *, command_options: CommandOptions | None = None) -> typing.Self:
        """Adds a resource command"""

    @abc.abstractmethod
    def with_relationship(self, resource_builder: AbstractResource, type: str) -> typing.Self:
        """Adds a relationship to another resource"""

    @abc.abstractmethod
    def with_parent_relationship(self, parent: AbstractResource) -> typing.Self:
        """Sets the parent relationship"""

    @abc.abstractmethod
    def with_child_relationship(self, child: AbstractResource) -> typing.Self:
        """Sets a child relationship"""

    @abc.abstractmethod
    def with_icon_name(self, icon_name: str, *, icon_variant: IconVariant = "Filled") -> typing.Self:
        """Sets the icon for the resource"""

    @abc.abstractmethod
    def exclude_from_mcp(self) -> typing.Self:
        """Excludes the resource from MCP server exposure"""

    @abc.abstractmethod
    def with_pipeline_step_factory(self, step_name: str, callback: typing.Callable[[PipelineStepContext], None], *, depends_on: typing.Iterable[str] | None = None, required_by: typing.Iterable[str] | None = None, tags: typing.Iterable[str] | None = None, description: str | None = None) -> typing.Self:
        """Adds a pipeline step to the resource"""

    @abc.abstractmethod
    def with_pipeline_config(self, callback: typing.Callable[[PipelineConfigurationContext], None]) -> typing.Self:
        """Configures pipeline step dependencies via a callback"""

    @abc.abstractmethod
    def get_resource_name(self) -> str:
        """Gets the resource name"""

    @abc.abstractmethod
    def on_before_resource_started(self, callback: typing.Callable[[BeforeResourceStartedEvent], None]) -> typing.Self:
        """Subscribes to the BeforeResourceStarted event"""

    @abc.abstractmethod
    def on_resource_stopped(self, callback: typing.Callable[[ResourceStoppedEvent], None]) -> typing.Self:
        """Subscribes to the ResourceStopped event"""

    @abc.abstractmethod
    def on_initialize_resource(self, callback: typing.Callable[[InitializeResourceEvent], None]) -> typing.Self:
        """Subscribes to the InitializeResource event"""

    @abc.abstractmethod
    def on_resource_ready(self, callback: typing.Callable[[ResourceReadyEvent], None]) -> typing.Self:
        """Subscribes to the ResourceReady event"""

    @abc.abstractmethod
    def create_execution_config(self) -> AbstractExecutionConfigurationBuilder:
        """Creates an execution configuration builder"""

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

    @abc.abstractmethod
    def with_image_push_options(self, callback: typing.Callable[[ContainerImagePushOptionsCallbackContext], None]) -> typing.Self:
        """Sets image push options via callback"""

    @abc.abstractmethod
    def with_remote_image_name(self, remote_image_name: str) -> typing.Self:
        """Sets the remote image name for publishing"""

    @abc.abstractmethod
    def with_remote_image_tag(self, remote_image_tag: str) -> typing.Self:
        """Sets the remote image tag for publishing"""


class AbstractContainerFilesDestinationResource(AbstractResource):
    """Abstract base class for AbstractContainerFilesDestinationResource interface."""

    @abc.abstractmethod
    def publish_with_container_files(self, source: AbstractResourceWithContainerFiles, destination_path: str) -> typing.Self:
        """Configures the resource to copy container files from the specified source during publishing"""


class AbstractResourceWithArgs(AbstractResource):
    """Abstract base class for AbstractResourceWithArgs interface."""

    @abc.abstractmethod
    def with_args(self, args: typing.Iterable[str]) -> typing.Self:
        """Adds arguments"""

    @abc.abstractmethod
    def with_args_callback(self, callback: typing.Callable[[CommandLineArgsCallbackContext], None]) -> typing.Self:
        """Sets command-line arguments via callback"""


class AbstractResourceWithConnectionString(AbstractResource, AbstractExpressionValue, AbstractValueWithReferences):
    """Abstract base class for AbstractResourceWithConnectionString interface."""

    @abc.abstractmethod
    def with_connection_property(self, name: str, value: str | ReferenceExpression) -> typing.Self:
        """Adds a connection property with a string or reference expression value"""

    @abc.abstractmethod
    def get_connection_property(self, key: str) -> ReferenceExpression:
        """Gets a connection property by key"""

    @abc.abstractmethod
    def on_connection_string_available(self, callback: typing.Callable[[ConnectionStringAvailableEvent], None]) -> typing.Self:
        """Subscribes to the ConnectionStringAvailable event"""

    @abc.abstractmethod
    def with_connection_string(self, connection_string: ReferenceExpression) -> typing.Self:
        """Sets the connection string using a reference expression"""

    @abc.abstractmethod
    def with_connection_string_direct(self, connection_string: str) -> typing.Self:
        """Sets connection string using direct interface target"""


class AbstractResourceWithContainerFiles(AbstractResource):
    """Abstract base class for AbstractResourceWithContainerFiles interface."""

    @abc.abstractmethod
    def with_container_files_source(self, source_path: str) -> typing.Self:
        """Sets the source directory for container files"""

    @abc.abstractmethod
    def clear_container_files_sources(self) -> typing.Self:
        """Clears all container file sources"""


class AbstractResourceWithEndpoints(AbstractResource):
    """Abstract base class for AbstractResourceWithEndpoints interface."""

    @abc.abstractmethod
    def with_mcp_server(self, *, path: str = "/mcp", endpoint_name: str | None = None) -> typing.Self:
        """Configures an MCP server endpoint on the resource"""

    @abc.abstractmethod
    def with_endpoint_callback(self, endpoint_name: str, callback: typing.Callable[[EndpointUpdateContext], None], *, create_if_not_exists: bool = True) -> typing.Self:
        """Updates a named endpoint via callback"""

    @abc.abstractmethod
    def with_http_endpoint_callback(self, callback: typing.Callable[[EndpointUpdateContext], None], *, name: str | None = None, create_if_not_exists: bool = True) -> typing.Self:
        """Updates an HTTP endpoint via callback"""

    @abc.abstractmethod
    def with_https_endpoint_callback(self, callback: typing.Callable[[EndpointUpdateContext], None], *, name: str | None = None, create_if_not_exists: bool = True) -> typing.Self:
        """Updates an HTTPS endpoint via callback"""

    @abc.abstractmethod
    def with_endpoint(self, *, port: int | None = None, target_port: int | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> typing.Self:
        """Adds a network endpoint"""

    @abc.abstractmethod
    def with_http_endpoint(self, *, port: int | None = None, target_port: int | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> typing.Self:
        """Adds an HTTP endpoint"""

    @abc.abstractmethod
    def with_https_endpoint(self, *, port: int | None = None, target_port: int | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> typing.Self:
        """Adds an HTTPS endpoint"""

    @abc.abstractmethod
    def with_external_http_endpoints(self) -> typing.Self:
        """Makes HTTP endpoints externally accessible"""

    @abc.abstractmethod
    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""

    @abc.abstractmethod
    def as_http2_service(self) -> typing.Self:
        """Configures resource for HTTP/2"""

    @abc.abstractmethod
    def with_http_health_check(self, *, path: str | None = None, status_code: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health check"""

    @abc.abstractmethod
    def with_http_command(self, path: str, display_name: str, *, options: HttpCommandExportOptions | None = None) -> typing.Self:
        """Adds an HTTP resource command"""

    @abc.abstractmethod
    def with_http_probe(self, probe_type: ProbeType, *, path: str | None = None, initial_delay_seconds: int | None = None, period_seconds: int | None = None, timeout_seconds: int | None = None, failure_threshold: int | None = None, success_threshold: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health probe to the resource"""

    @abc.abstractmethod
    def on_resource_endpoints_allocated(self, callback: typing.Callable[[ResourceEndpointsAllocatedEvent], None]) -> typing.Self:
        """Subscribes to the ResourceEndpointsAllocated event"""


class AbstractResourceWithEnvironment(AbstractResource):
    """Abstract base class for AbstractResourceWithEnvironment interface."""

    @abc.abstractmethod
    def with_otlp_exporter(self, *, protocol: OtlpProtocol | None = None) -> typing.Self:
        """Configures OTLP telemetry export"""

    @abc.abstractmethod
    def with_env(self, name: str, value: str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue) -> typing.Self:
        """Sets an environment variable"""

    @abc.abstractmethod
    def with_env_callback(self, callback: typing.Callable[[EnvironmentCallbackContext], None]) -> typing.Self:
        """Sets environment variables via callback"""

    @abc.abstractmethod
    def with_reference_env(self, options: ReferenceEnvironmentInjectionOptions) -> typing.Self:
        """Configures which reference values are injected into environment variables"""

    @abc.abstractmethod
    def with_reference(self, source: AbstractResource | EndpointReference | str, *, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> typing.Self:
        """Adds a reference to another resource"""

    @abc.abstractmethod
    def with_developer_certificate_trust(self, trust: bool) -> typing.Self:
        """Configures developer certificate trust"""

    @abc.abstractmethod
    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> typing.Self:
        """Sets the certificate trust scope"""

    @abc.abstractmethod
    def with_https_developer_certificate(self, *, password: ParameterResource | None = None) -> typing.Self:
        """Configures HTTPS with a developer certificate"""

    @abc.abstractmethod
    def without_https_certificate(self) -> typing.Self:
        """Removes HTTPS certificate configuration"""

    @abc.abstractmethod
    def test_with_env_callback(self, callback: typing.Callable[[TestEnvironmentContext], None]) -> typing.Self:
        """Configures environment with callback (test version)"""

    @abc.abstractmethod
    def with_env_vars(self, vars: typing.Mapping[str, str]) -> typing.Self:
        """Sets environment variables"""


class AbstractResourceWithProbes(AbstractResource):
    """Abstract base class for AbstractResourceWithProbes interface."""


class AbstractResourceWithServiceDiscovery(AbstractResourceWithEndpoints):
    """Abstract base class for AbstractResourceWithServiceDiscovery interface."""


class AbstractResourceWithWaitSupport(AbstractResource):
    """Abstract base class for AbstractResourceWithWaitSupport interface."""

    @abc.abstractmethod
    def wait_for(self, dependency: AbstractResource, *, wait_behavior: WaitBehavior | None = None) -> typing.Self:
        """Waits for another resource to be ready"""

    @abc.abstractmethod
    def wait_for_start(self, dependency: AbstractResource, *, wait_behavior: WaitBehavior | None = None) -> typing.Self:
        """Waits for another resource to start"""

    @abc.abstractmethod
    def wait_for_completion(self, dependency: AbstractResource, *, exit_code: int = 0) -> typing.Self:
        """Waits for resource completion"""


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

    container_registry: AbstractResource
    dockerfile_base_image: DockerfileBaseImageParameters | typing.Literal[True]
    required_command: str | tuple[str, str]
    urls: typing.Callable[[ResourceUrlsCallbackContext], None]
    url: str | ReferenceExpression | tuple[str | ReferenceExpression, str]
    url_for_endpoint: tuple[str, typing.Callable[[ResourceUrlAnnotation], None]]
    exclude_from_manifest: typing.Literal[True]
    explicit_start: typing.Literal[True]
    health_check: str
    command: tuple[str, str, typing.Callable[[ExecuteCommandContext], ExecuteCommandResult]] | CommandParameters
    relationship: tuple[AbstractResource, str]
    parent_relationship: AbstractResource
    child_relationship: AbstractResource
    icon_name: str | tuple[str, IconVariant]
    exclude_from_mcp: typing.Literal[True]
    pipeline_step_factory: tuple[str, typing.Callable[[PipelineStepContext], None]] | PipelineStepFactoryParameters
    pipeline_config: typing.Callable[[PipelineConfigurationContext], None]
    on_before_resource_started: typing.Callable[[BeforeResourceStartedEvent], None]
    on_resource_stopped: typing.Callable[[ResourceStoppedEvent], None]
    on_initialize_resource: typing.Callable[[InitializeResourceEvent], None]
    on_resource_ready: typing.Callable[[ResourceReadyEvent], None]
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

    def with_container_registry(self, registry: AbstractResource) -> typing.Self:
        """Configures a resource to use a container registry"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['registry'] = registry
        result = self._client.invoke_capability(
            'Aspire.Hosting/withContainerRegistry',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_dockerfile_base_image(self, *, build_image: str | None = None, runtime_image: str | None = None) -> typing.Self:
        """Sets the base image for a Dockerfile build"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if build_image is not None:
            rpc_args['buildImage'] = build_image
        if runtime_image is not None:
            rpc_args['runtimeImage'] = runtime_image
        result = self._client.invoke_capability(
            'Aspire.Hosting/withDockerfileBaseImage',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_required_command(self, command: str, *, help_link: str | None = None) -> typing.Self:
        """Adds a required command dependency"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['command'] = command
        if help_link is not None:
            rpc_args['helpLink'] = help_link
        result = self._client.invoke_capability(
            'Aspire.Hosting/withRequiredCommand',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_urls(self, callback: typing.Callable[[ResourceUrlsCallbackContext], None]) -> typing.Self:
        """Customizes displayed URLs via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withUrls',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_url(self, url: str | ReferenceExpression, *, display_text: str | None = None) -> typing.Self:
        """Adds or modifies displayed URLs"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['url'] = url
        if display_text is not None:
            rpc_args['displayText'] = display_text
        result = self._client.invoke_capability(
            'Aspire.Hosting/withUrl',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_url_for_endpoint(self, endpoint_name: str, callback: typing.Callable[[ResourceUrlAnnotation], None]) -> typing.Self:
        """Customizes the URL for a specific endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['endpointName'] = endpoint_name
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withUrlForEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def exclude_from_manifest(self) -> typing.Self:
        """Excludes the resource from the deployment manifest"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/excludeFromManifest',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_explicit_start(self) -> typing.Self:
        """Prevents resource from starting automatically"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withExplicitStart',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_health_check(self, key: str) -> typing.Self:
        """Adds a health check by key"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['key'] = key
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHealthCheck',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_command(self, name: str, display_name: str, execute_command: typing.Callable[[ExecuteCommandContext], ExecuteCommandResult], *, command_options: CommandOptions | None = None) -> typing.Self:
        """Adds a resource command"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['displayName'] = display_name
        rpc_args['executeCommand'] = self._client.register_callback(execute_command)
        if command_options is not None:
            rpc_args['commandOptions'] = command_options
        result = self._client.invoke_capability(
            'Aspire.Hosting/withCommand',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_relationship(self, resource_builder: AbstractResource, type: str) -> typing.Self:
        """Adds a relationship to another resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['resourceBuilder'] = resource_builder
        rpc_args['type'] = type
        result = self._client.invoke_capability(
            'Aspire.Hosting/withBuilderRelationship',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_parent_relationship(self, parent: AbstractResource) -> typing.Self:
        """Sets the parent relationship"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['parent'] = parent
        result = self._client.invoke_capability(
            'Aspire.Hosting/withBuilderParentRelationship',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_child_relationship(self, child: AbstractResource) -> typing.Self:
        """Sets a child relationship"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['child'] = child
        result = self._client.invoke_capability(
            'Aspire.Hosting/withBuilderChildRelationship',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_icon_name(self, icon_name: str, *, icon_variant: IconVariant = "Filled") -> typing.Self:
        """Sets the icon for the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['iconName'] = icon_name
        if icon_variant is not None:
            rpc_args['iconVariant'] = icon_variant
        result = self._client.invoke_capability(
            'Aspire.Hosting/withIconName',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def exclude_from_mcp(self) -> typing.Self:
        """Excludes the resource from MCP server exposure"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/excludeFromMcp',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_pipeline_step_factory(self, step_name: str, callback: typing.Callable[[PipelineStepContext], None], *, depends_on: typing.Iterable[str] | None = None, required_by: typing.Iterable[str] | None = None, tags: typing.Iterable[str] | None = None, description: str | None = None) -> typing.Self:
        """Adds a pipeline step to the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['stepName'] = step_name
        rpc_args['callback'] = self._client.register_callback(callback)
        if depends_on is not None:
            rpc_args['dependsOn'] = depends_on
        if required_by is not None:
            rpc_args['requiredBy'] = required_by
        if tags is not None:
            rpc_args['tags'] = tags
        if description is not None:
            rpc_args['description'] = description
        result = self._client.invoke_capability(
            'Aspire.Hosting/withPipelineStepFactory',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_pipeline_config(self, callback: typing.Callable[[PipelineConfigurationContext], None]) -> typing.Self:
        """Configures pipeline step dependencies via a callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withPipelineConfiguration',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        rpc_args: dict[str, typing.Any] = {'resource': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/getResourceName',
            rpc_args,
        )
        return typing.cast(str, result)

    def on_before_resource_started(self, callback: typing.Callable[[BeforeResourceStartedEvent], None]) -> typing.Self:
        """Subscribes to the BeforeResourceStarted event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/onBeforeResourceStarted',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def on_resource_stopped(self, callback: typing.Callable[[ResourceStoppedEvent], None]) -> typing.Self:
        """Subscribes to the ResourceStopped event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/onResourceStopped',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def on_initialize_resource(self, callback: typing.Callable[[InitializeResourceEvent], None]) -> typing.Self:
        """Subscribes to the InitializeResource event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/onInitializeResource',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def on_resource_ready(self, callback: typing.Callable[[ResourceReadyEvent], None]) -> typing.Self:
        """Subscribes to the ResourceReady event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/onResourceReady',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def create_execution_config(self) -> AbstractExecutionConfigurationBuilder:
        """Creates an execution configuration builder"""
        rpc_args: dict[str, typing.Any] = {'resource': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/createExecutionConfiguration',
            rpc_args,
        )
        return typing.cast(AbstractExecutionConfigurationBuilder, result)

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
        if _container_registry := kwargs.pop("container_registry", None):
            if _validate_type(_container_registry, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["registry"] = typing.cast(AbstractResource, _container_registry)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withContainerRegistry', rpc_args))
            else:
                raise TypeError("Invalid type for option 'container_registry'. Expected: AbstractResource")
        if _dockerfile_base_image := kwargs.pop("dockerfile_base_image", None):
            if _validate_dict_types(_dockerfile_base_image, DockerfileBaseImageParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["buildImage"] = typing.cast(DockerfileBaseImageParameters, _dockerfile_base_image).get("build_image")
                rpc_args["runtimeImage"] = typing.cast(DockerfileBaseImageParameters, _dockerfile_base_image).get("runtime_image")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDockerfileBaseImage', rpc_args))
            elif _dockerfile_base_image is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDockerfileBaseImage', rpc_args))
            else:
                raise TypeError("Invalid type for option 'dockerfile_base_image'. Expected: DockerfileBaseImageParameters or Literal[True]")
        if _required_command := kwargs.pop("required_command", None):
            if _validate_type(_required_command, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["command"] = typing.cast(str, _required_command)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withRequiredCommand', rpc_args))
            elif _validate_tuple_types(_required_command, (str, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["command"] = typing.cast(tuple[str, str], _required_command)[0]
                rpc_args["helpLink"] = typing.cast(tuple[str, str], _required_command)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withRequiredCommand', rpc_args))
            else:
                raise TypeError("Invalid type for option 'required_command'. Expected: str or (str, str)")
        if _urls := kwargs.pop("urls", None):
            if _validate_type(_urls, typing.Callable[[ResourceUrlsCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ResourceUrlsCallbackContext], None], _urls))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withUrls', rpc_args))
            else:
                raise TypeError("Invalid type for option 'urls'. Expected: Callable[[ResourceUrlsCallbackContext], None]")
        if _url := kwargs.pop("url", None):
            if _validate_type(_url, str | ReferenceExpression):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["url"] = typing.cast(str | ReferenceExpression, _url)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withUrl', rpc_args))
            elif _validate_tuple_types(_url, (str | ReferenceExpression, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["url"] = typing.cast(tuple[str | ReferenceExpression, str], _url)[0]
                rpc_args["displayText"] = typing.cast(tuple[str | ReferenceExpression, str], _url)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withUrl', rpc_args))
            else:
                raise TypeError("Invalid type for option 'url'. Expected: str | ReferenceExpression or (str | ReferenceExpression, str)")
        if _url_for_endpoint := kwargs.pop("url_for_endpoint", None):
            if _validate_tuple_types(_url_for_endpoint, (str, typing.Callable[[ResourceUrlAnnotation], None])):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(tuple[str, typing.Callable[[ResourceUrlAnnotation], None]], _url_for_endpoint)[0]
                rpc_args["callback"] = client.register_callback(typing.cast(tuple[str, typing.Callable[[ResourceUrlAnnotation], None]], _url_for_endpoint)[1])
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withUrlForEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'url_for_endpoint'. Expected: (str, Callable[[ResourceUrlAnnotation], None])")
        if _exclude_from_manifest := kwargs.pop("exclude_from_manifest", None):
            if _exclude_from_manifest is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/excludeFromManifest', rpc_args))
            else:
                raise TypeError("Invalid type for option 'exclude_from_manifest'. Expected: Literal[True]")
        if _explicit_start := kwargs.pop("explicit_start", None):
            if _explicit_start is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withExplicitStart', rpc_args))
            else:
                raise TypeError("Invalid type for option 'explicit_start'. Expected: Literal[True]")
        if _health_check := kwargs.pop("health_check", None):
            if _validate_type(_health_check, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["key"] = typing.cast(str, _health_check)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHealthCheck', rpc_args))
            else:
                raise TypeError("Invalid type for option 'health_check'. Expected: str")
        if _command := kwargs.pop("command", None):
            if _validate_tuple_types(_command, (str, str, typing.Callable[[ExecuteCommandContext], ExecuteCommandResult])):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(tuple[str, str, typing.Callable[[ExecuteCommandContext], ExecuteCommandResult]], _command)[0]
                rpc_args["displayName"] = typing.cast(tuple[str, str, typing.Callable[[ExecuteCommandContext], ExecuteCommandResult]], _command)[1]
                rpc_args["executeCommand"] = client.register_callback(typing.cast(tuple[str, str, typing.Callable[[ExecuteCommandContext], ExecuteCommandResult]], _command)[2])
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withCommand', rpc_args))
            elif _validate_dict_types(_command, CommandParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(CommandParameters, _command)["name"]
                rpc_args["displayName"] = typing.cast(CommandParameters, _command)["display_name"]
                rpc_args["executeCommand"] = client.register_callback(typing.cast(CommandParameters, _command)["execute_command"])
                rpc_args["commandOptions"] = typing.cast(CommandParameters, _command).get("command_options")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withCommand', rpc_args))
            else:
                raise TypeError("Invalid type for option 'command'. Expected: (str, str, Callable[[ExecuteCommandContext], ExecuteCommandResult]) or CommandParameters")
        if _relationship := kwargs.pop("relationship", None):
            if _validate_tuple_types(_relationship, (AbstractResource, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["resourceBuilder"] = typing.cast(tuple[AbstractResource, str], _relationship)[0]
                rpc_args["type"] = typing.cast(tuple[AbstractResource, str], _relationship)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withBuilderRelationship', rpc_args))
            else:
                raise TypeError("Invalid type for option 'relationship'. Expected: (AbstractResource, str)")
        if _parent_relationship := kwargs.pop("parent_relationship", None):
            if _validate_type(_parent_relationship, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["parent"] = typing.cast(AbstractResource, _parent_relationship)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withBuilderParentRelationship', rpc_args))
            else:
                raise TypeError("Invalid type for option 'parent_relationship'. Expected: AbstractResource")
        if _child_relationship := kwargs.pop("child_relationship", None):
            if _validate_type(_child_relationship, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["child"] = typing.cast(AbstractResource, _child_relationship)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withBuilderChildRelationship', rpc_args))
            else:
                raise TypeError("Invalid type for option 'child_relationship'. Expected: AbstractResource")
        if _icon_name := kwargs.pop("icon_name", None):
            if _validate_type(_icon_name, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["iconName"] = typing.cast(str, _icon_name)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withIconName', rpc_args))
            elif _validate_tuple_types(_icon_name, (str, IconVariant)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["iconName"] = typing.cast(tuple[str, IconVariant], _icon_name)[0]
                rpc_args["iconVariant"] = typing.cast(tuple[str, IconVariant], _icon_name)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withIconName', rpc_args))
            else:
                raise TypeError("Invalid type for option 'icon_name'. Expected: str or (str, IconVariant)")
        if _exclude_from_mcp := kwargs.pop("exclude_from_mcp", None):
            if _exclude_from_mcp is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/excludeFromMcp', rpc_args))
            else:
                raise TypeError("Invalid type for option 'exclude_from_mcp'. Expected: Literal[True]")
        if _pipeline_step_factory := kwargs.pop("pipeline_step_factory", None):
            if _validate_tuple_types(_pipeline_step_factory, (str, typing.Callable[[PipelineStepContext], None])):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["stepName"] = typing.cast(tuple[str, typing.Callable[[PipelineStepContext], None]], _pipeline_step_factory)[0]
                rpc_args["callback"] = client.register_callback(typing.cast(tuple[str, typing.Callable[[PipelineStepContext], None]], _pipeline_step_factory)[1])
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withPipelineStepFactory', rpc_args))
            elif _validate_dict_types(_pipeline_step_factory, PipelineStepFactoryParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["stepName"] = typing.cast(PipelineStepFactoryParameters, _pipeline_step_factory)["step_name"]
                rpc_args["callback"] = client.register_callback(typing.cast(PipelineStepFactoryParameters, _pipeline_step_factory)["callback"])
                rpc_args["dependsOn"] = typing.cast(PipelineStepFactoryParameters, _pipeline_step_factory).get("depends_on")
                rpc_args["requiredBy"] = typing.cast(PipelineStepFactoryParameters, _pipeline_step_factory).get("required_by")
                rpc_args["tags"] = typing.cast(PipelineStepFactoryParameters, _pipeline_step_factory).get("tags")
                rpc_args["description"] = typing.cast(PipelineStepFactoryParameters, _pipeline_step_factory).get("description")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withPipelineStepFactory', rpc_args))
            else:
                raise TypeError("Invalid type for option 'pipeline_step_factory'. Expected: (str, Callable[[PipelineStepContext], None]) or PipelineStepFactoryParameters")
        if _pipeline_config := kwargs.pop("pipeline_config", None):
            if _validate_type(_pipeline_config, typing.Callable[[PipelineConfigurationContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[PipelineConfigurationContext], None], _pipeline_config))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withPipelineConfiguration', rpc_args))
            else:
                raise TypeError("Invalid type for option 'pipeline_config'. Expected: Callable[[PipelineConfigurationContext], None]")
        if _on_before_resource_started := kwargs.pop("on_before_resource_started", None):
            if _validate_type(_on_before_resource_started, typing.Callable[[BeforeResourceStartedEvent], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[BeforeResourceStartedEvent], None], _on_before_resource_started))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/onBeforeResourceStarted', rpc_args))
            else:
                raise TypeError("Invalid type for option 'on_before_resource_started'. Expected: Callable[[BeforeResourceStartedEvent], None]")
        if _on_resource_stopped := kwargs.pop("on_resource_stopped", None):
            if _validate_type(_on_resource_stopped, typing.Callable[[ResourceStoppedEvent], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ResourceStoppedEvent], None], _on_resource_stopped))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/onResourceStopped', rpc_args))
            else:
                raise TypeError("Invalid type for option 'on_resource_stopped'. Expected: Callable[[ResourceStoppedEvent], None]")
        if _on_initialize_resource := kwargs.pop("on_initialize_resource", None):
            if _validate_type(_on_initialize_resource, typing.Callable[[InitializeResourceEvent], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[InitializeResourceEvent], None], _on_initialize_resource))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/onInitializeResource', rpc_args))
            else:
                raise TypeError("Invalid type for option 'on_initialize_resource'. Expected: Callable[[InitializeResourceEvent], None]")
        if _on_resource_ready := kwargs.pop("on_resource_ready", None):
            if _validate_type(_on_resource_ready, typing.Callable[[ResourceReadyEvent], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ResourceReadyEvent], None], _on_resource_ready))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/onResourceReady', rpc_args))
            else:
                raise TypeError("Invalid type for option 'on_resource_ready'. Expected: Callable[[ResourceReadyEvent], None]")
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


class ContainerRegistryResourceKwargs(_BaseResourceKwargs, total=False):
    """ContainerRegistryResource options."""


class ContainerRegistryResource(_BaseResource, AbstractContainerRegistry):
    """ContainerRegistryResource resource."""

    def __repr__(self) -> str:
        return "ContainerRegistryResource(handle={self._handle.handle_id})"

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[ContainerRegistryResourceKwargs]) -> None:
        super().__init__(handle, client, **kwargs)


class ContainerResourceKwargs(_BaseResourceKwargs, total=False):
    """ContainerResource options."""

    bind_mount: tuple[str, str] | BindMountParameters
    entrypoint: str
    image_tag: str
    image_registry: str
    image: str | tuple[str, str]
    image_sha256: str
    container_runtime_args: typing.Iterable[str]
    lifetime: ContainerLifetime
    image_pull_policy: ImagePullPolicy
    publish_as_container: typing.Literal[True]
    dockerfile: str | DockerfileParameters
    container_name: str
    build_arg: tuple[str, str | ParameterResource]
    build_secret: tuple[str, ParameterResource]
    container_certificate_paths: ContainerCertificatePathsParameters | typing.Literal[True]
    endpoint_proxy_support: bool
    dockerfile_builder: tuple[str, typing.Callable[[DockerfileBuilderCallbackContext], None]] | DockerfileBuilderParameters
    container_network_alias: str
    mcp_server: McpServerParameters | typing.Literal[True]
    otlp_exporter: OtlpProtocol | typing.Literal[True]
    publish_as_connection_string: typing.Literal[True]
    env: tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue]
    env_callback: typing.Callable[[EnvironmentCallbackContext], None]
    args: typing.Iterable[str]
    args_callback: typing.Callable[[CommandLineArgsCallbackContext], None]
    reference_env: ReferenceEnvironmentInjectionOptions
    reference: AbstractResource | EndpointReference | str | ReferenceParameters
    endpoint_callback: tuple[str, typing.Callable[[EndpointUpdateContext], None]] | EndpointCallbackParameters
    http_endpoint_callback: typing.Callable[[EndpointUpdateContext], None] | HttpEndpointCallbackParameters
    https_endpoint_callback: typing.Callable[[EndpointUpdateContext], None] | HttpsEndpointCallbackParameters
    endpoint: EndpointParameters | typing.Literal[True]
    http_endpoint: HttpEndpointParameters | typing.Literal[True]
    https_endpoint: HttpsEndpointParameters | typing.Literal[True]
    external_http_endpoints: typing.Literal[True]
    as_http2_service: typing.Literal[True]
    wait_for: AbstractResource | tuple[AbstractResource, WaitBehavior]
    wait_for_start: AbstractResource | tuple[AbstractResource, WaitBehavior]
    wait_for_completion: AbstractResource | tuple[AbstractResource, int]
    http_health_check: HttpHealthCheckParameters | typing.Literal[True]
    http_command: tuple[str, str] | HttpCommandParameters
    developer_certificate_trust: bool
    certificate_trust_scope: CertificateTrustScope
    https_developer_certificate: ParameterResource | typing.Literal[True]
    without_https_certificate: typing.Literal[True]
    http_probe: ProbeType | HttpProbeParameters
    image_push_options: typing.Callable[[ContainerImagePushOptionsCallbackContext], None]
    remote_image_name: str
    remote_image_tag: str
    volume: str | VolumeParameters
    on_resource_endpoints_allocated: typing.Callable[[ResourceEndpointsAllocatedEvent], None]
    test_with_env_callback: typing.Callable[[TestEnvironmentContext], None]
    env_vars: typing.Mapping[str, str]

class ContainerResource(_BaseResource, AbstractResourceWithEnvironment, AbstractResourceWithArgs, AbstractResourceWithEndpoints, AbstractResourceWithWaitSupport, AbstractResourceWithProbes, AbstractComputeResource):
    """ContainerResource resource."""

    def __repr__(self) -> str:
        return "ContainerResource(handle={self._handle.handle_id})"

    def with_bind_mount(self, source: str, target: str, *, is_read_only: bool = False) -> typing.Self:
        """Adds a bind mount"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['source'] = source
        rpc_args['target'] = target
        if is_read_only is not None:
            rpc_args['isReadOnly'] = is_read_only
        result = self._client.invoke_capability(
            'Aspire.Hosting/withBindMount',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_entrypoint(self, entrypoint: str) -> typing.Self:
        """Sets the container entrypoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['entrypoint'] = entrypoint
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEntrypoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_image_tag(self, tag: str) -> typing.Self:
        """Sets the container image tag"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['tag'] = tag
        result = self._client.invoke_capability(
            'Aspire.Hosting/withImageTag',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_image_registry(self, registry: str) -> typing.Self:
        """Sets the container image registry"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['registry'] = registry
        result = self._client.invoke_capability(
            'Aspire.Hosting/withImageRegistry',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_image(self, image: str, *, tag: str | None = None) -> typing.Self:
        """Sets the container image"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['image'] = image
        if tag is not None:
            rpc_args['tag'] = tag
        result = self._client.invoke_capability(
            'Aspire.Hosting/withImage',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_image_sha256(self, sha256: str) -> typing.Self:
        """Sets the image SHA256 digest"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['sha256'] = sha256
        result = self._client.invoke_capability(
            'Aspire.Hosting/withImageSHA256',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_container_runtime_args(self, args: typing.Iterable[str]) -> typing.Self:
        """Adds runtime arguments for the container"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['args'] = args
        result = self._client.invoke_capability(
            'Aspire.Hosting/withContainerRuntimeArgs',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_lifetime(self, lifetime: ContainerLifetime) -> typing.Self:
        """Sets the lifetime behavior of the container resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['lifetime'] = lifetime
        result = self._client.invoke_capability(
            'Aspire.Hosting/withLifetime',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_image_pull_policy(self, pull_policy: ImagePullPolicy) -> typing.Self:
        """Sets the container image pull policy"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['pullPolicy'] = pull_policy
        result = self._client.invoke_capability(
            'Aspire.Hosting/withImagePullPolicy',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def publish_as_container(self) -> typing.Self:
        """Configures the resource to be published as a container"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/publishAsContainer',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_dockerfile(self, context_path: str, *, dockerfile_path: str | None = None, stage: str | None = None) -> typing.Self:
        """Configures the resource to use a Dockerfile"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['contextPath'] = context_path
        if dockerfile_path is not None:
            rpc_args['dockerfilePath'] = dockerfile_path
        if stage is not None:
            rpc_args['stage'] = stage
        result = self._client.invoke_capability(
            'Aspire.Hosting/withDockerfile',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_container_name(self, name: str) -> typing.Self:
        """Sets the container name"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withContainerName',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_build_arg(self, name: str, value: str | ParameterResource) -> typing.Self:
        """Adds a build argument from a string value or parameter resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting/withBuildArg',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_build_secret(self, name: str, value: ParameterResource) -> typing.Self:
        """Adds a build secret from a parameter resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting/withParameterBuildSecret',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_container_certificate_paths(self, *, custom_certificates_destination: str | None = None, default_certificate_bundle_paths: typing.Iterable[str] | None = None, default_certificate_dir_paths: typing.Iterable[str] | None = None) -> typing.Self:
        """Overrides container certificate bundle and directory paths used for trust configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if custom_certificates_destination is not None:
            rpc_args['customCertificatesDestination'] = custom_certificates_destination
        if default_certificate_bundle_paths is not None:
            rpc_args['defaultCertificateBundlePaths'] = default_certificate_bundle_paths
        if default_certificate_dir_paths is not None:
            rpc_args['defaultCertificateDirectoryPaths'] = default_certificate_dir_paths
        result = self._client.invoke_capability(
            'Aspire.Hosting/withContainerCertificatePaths',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_endpoint_proxy_support(self, proxy_enabled: bool) -> typing.Self:
        """Configures endpoint proxy support"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['proxyEnabled'] = proxy_enabled
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEndpointProxySupport',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_dockerfile_builder(self, context_path: str, callback: typing.Callable[[DockerfileBuilderCallbackContext], None], *, stage: str | None = None) -> typing.Self:
        """Configures the resource to use a programmatically generated Dockerfile"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['contextPath'] = context_path
        rpc_args['callback'] = self._client.register_callback(callback)
        if stage is not None:
            rpc_args['stage'] = stage
        result = self._client.invoke_capability(
            'Aspire.Hosting/withDockerfileBuilder',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_container_network_alias(self, alias: str) -> typing.Self:
        """Adds a network alias for the container"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['alias'] = alias
        result = self._client.invoke_capability(
            'Aspire.Hosting/withContainerNetworkAlias',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_mcp_server(self, *, path: str = "/mcp", endpoint_name: str | None = None) -> typing.Self:
        """Configures an MCP server endpoint on the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if path is not None:
            rpc_args['path'] = path
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withMcpServer',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_otlp_exporter(self, *, protocol: OtlpProtocol | None = None) -> typing.Self:
        """Configures OTLP telemetry export"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if protocol is not None:
            rpc_args['protocol'] = protocol
        result = self._client.invoke_capability(
            'Aspire.Hosting/withOtlpExporter',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def publish_as_connection_string(self) -> typing.Self:
        """Publishes the resource as a connection string"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/publishAsConnectionString',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_env(self, name: str, value: str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue) -> typing.Self:
        """Sets an environment variable"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEnvironment',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_env_callback(self, callback: typing.Callable[[EnvironmentCallbackContext], None]) -> typing.Self:
        """Sets environment variables via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEnvironmentCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_args(self, args: typing.Iterable[str]) -> typing.Self:
        """Adds arguments"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['args'] = args
        result = self._client.invoke_capability(
            'Aspire.Hosting/withArgs',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_args_callback(self, callback: typing.Callable[[CommandLineArgsCallbackContext], None]) -> typing.Self:
        """Sets command-line arguments via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withArgsCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_reference_env(self, options: ReferenceEnvironmentInjectionOptions) -> typing.Self:
        """Configures which reference values are injected into environment variables"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['options'] = options
        result = self._client.invoke_capability(
            'Aspire.Hosting/withReferenceEnvironment',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_reference(self, source: AbstractResource | EndpointReference | str, *, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> typing.Self:
        """Adds a reference to another resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['source'] = source
        if connection_name is not None:
            rpc_args['connectionName'] = connection_name
        if optional is not None:
            rpc_args['optional'] = optional
        if name is not None:
            rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withReference',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_endpoint_callback(self, endpoint_name: str, callback: typing.Callable[[EndpointUpdateContext], None], *, create_if_not_exists: bool = True) -> typing.Self:
        """Updates a named endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['endpointName'] = endpoint_name
        rpc_args['callback'] = self._client.register_callback(callback)
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_endpoint_callback(self, callback: typing.Callable[[EndpointUpdateContext], None], *, name: str | None = None, create_if_not_exists: bool = True) -> typing.Self:
        """Updates an HTTP endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        if name is not None:
            rpc_args['name'] = name
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_endpoint_callback(self, callback: typing.Callable[[EndpointUpdateContext], None], *, name: str | None = None, create_if_not_exists: bool = True) -> typing.Self:
        """Updates an HTTPS endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        if name is not None:
            rpc_args['name'] = name
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpsEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_endpoint(self, *, port: int | None = None, target_port: int | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> typing.Self:
        """Adds a network endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if scheme is not None:
            rpc_args['scheme'] = scheme
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        if is_external is not None:
            rpc_args['isExternal'] = is_external
        if protocol is not None:
            rpc_args['protocol'] = protocol
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_endpoint(self, *, port: int | None = None, target_port: int | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> typing.Self:
        """Adds an HTTP endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_endpoint(self, *, port: int | None = None, target_port: int | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> typing.Self:
        """Adds an HTTPS endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpsEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_external_http_endpoints(self) -> typing.Self:
        """Makes HTTP endpoints externally accessible"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withExternalHttpEndpoints',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/getEndpoint',
            rpc_args,
        )
        return typing.cast(EndpointReference, result)

    def as_http2_service(self) -> typing.Self:
        """Configures resource for HTTP/2"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/asHttp2Service',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for(self, dependency: AbstractResource, *, wait_behavior: WaitBehavior | None = None) -> typing.Self:
        """Waits for another resource to be ready"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if wait_behavior is not None:
            rpc_args['waitBehavior'] = wait_behavior
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitFor',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for_start(self, dependency: AbstractResource, *, wait_behavior: WaitBehavior | None = None) -> typing.Self:
        """Waits for another resource to start"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if wait_behavior is not None:
            rpc_args['waitBehavior'] = wait_behavior
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitForStart',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for_completion(self, dependency: AbstractResource, *, exit_code: int = 0) -> typing.Self:
        """Waits for resource completion"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if exit_code is not None:
            rpc_args['exitCode'] = exit_code
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitForResourceCompletion',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_health_check(self, *, path: str | None = None, status_code: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health check"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if path is not None:
            rpc_args['path'] = path
        if status_code is not None:
            rpc_args['statusCode'] = status_code
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpHealthCheck',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_command(self, path: str, display_name: str, *, options: HttpCommandExportOptions | None = None) -> typing.Self:
        """Adds an HTTP resource command"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['path'] = path
        rpc_args['displayName'] = display_name
        if options is not None:
            rpc_args['options'] = options
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpCommand',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_developer_certificate_trust(self, trust: bool) -> typing.Self:
        """Configures developer certificate trust"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['trust'] = trust
        result = self._client.invoke_capability(
            'Aspire.Hosting/withDeveloperCertificateTrust',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> typing.Self:
        """Sets the certificate trust scope"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['scope'] = scope
        result = self._client.invoke_capability(
            'Aspire.Hosting/withCertificateTrustScope',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_developer_certificate(self, *, password: ParameterResource | None = None) -> typing.Self:
        """Configures HTTPS with a developer certificate"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if password is not None:
            rpc_args['password'] = password
        result = self._client.invoke_capability(
            'Aspire.Hosting/withParameterHttpsDeveloperCertificate',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def without_https_certificate(self) -> typing.Self:
        """Removes HTTPS certificate configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withoutHttpsCertificate',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_probe(self, probe_type: ProbeType, *, path: str | None = None, initial_delay_seconds: int | None = None, period_seconds: int | None = None, timeout_seconds: int | None = None, failure_threshold: int | None = None, success_threshold: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health probe to the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['probeType'] = probe_type
        if path is not None:
            rpc_args['path'] = path
        if initial_delay_seconds is not None:
            rpc_args['initialDelaySeconds'] = initial_delay_seconds
        if period_seconds is not None:
            rpc_args['periodSeconds'] = period_seconds
        if timeout_seconds is not None:
            rpc_args['timeoutSeconds'] = timeout_seconds
        if failure_threshold is not None:
            rpc_args['failureThreshold'] = failure_threshold
        if success_threshold is not None:
            rpc_args['successThreshold'] = success_threshold
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpProbe',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_image_push_options(self, callback: typing.Callable[[ContainerImagePushOptionsCallbackContext], None]) -> typing.Self:
        """Sets image push options via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withImagePushOptions',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_remote_image_name(self, remote_image_name: str) -> typing.Self:
        """Sets the remote image name for publishing"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['remoteImageName'] = remote_image_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withRemoteImageName',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_remote_image_tag(self, remote_image_tag: str) -> typing.Self:
        """Sets the remote image tag for publishing"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['remoteImageTag'] = remote_image_tag
        result = self._client.invoke_capability(
            'Aspire.Hosting/withRemoteImageTag',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_volume(self, target: str, *, name: str | None = None, is_read_only: bool = False) -> typing.Self:
        """Adds a volume"""
        rpc_args: dict[str, typing.Any] = {'resource': self._handle}
        rpc_args['target'] = target
        if name is not None:
            rpc_args['name'] = name
        if is_read_only is not None:
            rpc_args['isReadOnly'] = is_read_only
        result = self._client.invoke_capability(
            'Aspire.Hosting/withVolume',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def on_resource_endpoints_allocated(self, callback: typing.Callable[[ResourceEndpointsAllocatedEvent], None]) -> typing.Self:
        """Subscribes to the ResourceEndpointsAllocated event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/onResourceEndpointsAllocated',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

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
        if _bind_mount := kwargs.pop("bind_mount", None):
            if _validate_tuple_types(_bind_mount, (str, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(tuple[str, str], _bind_mount)[0]
                rpc_args["target"] = typing.cast(tuple[str, str], _bind_mount)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withBindMount', rpc_args))
            elif _validate_dict_types(_bind_mount, BindMountParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(BindMountParameters, _bind_mount)["source"]
                rpc_args["target"] = typing.cast(BindMountParameters, _bind_mount)["target"]
                rpc_args["isReadOnly"] = typing.cast(BindMountParameters, _bind_mount).get("is_read_only")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withBindMount', rpc_args))
            else:
                raise TypeError("Invalid type for option 'bind_mount'. Expected: (str, str) or BindMountParameters")
        if _entrypoint := kwargs.pop("entrypoint", None):
            if _validate_type(_entrypoint, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["entrypoint"] = typing.cast(str, _entrypoint)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEntrypoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'entrypoint'. Expected: str")
        if _image_tag := kwargs.pop("image_tag", None):
            if _validate_type(_image_tag, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["tag"] = typing.cast(str, _image_tag)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImageTag', rpc_args))
            else:
                raise TypeError("Invalid type for option 'image_tag'. Expected: str")
        if _image_registry := kwargs.pop("image_registry", None):
            if _validate_type(_image_registry, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["registry"] = typing.cast(str, _image_registry)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImageRegistry', rpc_args))
            else:
                raise TypeError("Invalid type for option 'image_registry'. Expected: str")
        if _image := kwargs.pop("image", None):
            if _validate_type(_image, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["image"] = typing.cast(str, _image)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImage', rpc_args))
            elif _validate_tuple_types(_image, (str, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["image"] = typing.cast(tuple[str, str], _image)[0]
                rpc_args["tag"] = typing.cast(tuple[str, str], _image)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImage', rpc_args))
            else:
                raise TypeError("Invalid type for option 'image'. Expected: str or (str, str)")
        if _image_sha256 := kwargs.pop("image_sha256", None):
            if _validate_type(_image_sha256, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["sha256"] = typing.cast(str, _image_sha256)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImageSHA256', rpc_args))
            else:
                raise TypeError("Invalid type for option 'image_sha256'. Expected: str")
        if _container_runtime_args := kwargs.pop("container_runtime_args", None):
            if _validate_type(_container_runtime_args, typing.Iterable[str]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["args"] = typing.cast(typing.Iterable[str], _container_runtime_args)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withContainerRuntimeArgs', rpc_args))
            else:
                raise TypeError("Invalid type for option 'container_runtime_args'. Expected: Iterable[str]")
        if _lifetime := kwargs.pop("lifetime", None):
            if _validate_type(_lifetime, ContainerLifetime):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["lifetime"] = typing.cast(ContainerLifetime, _lifetime)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withLifetime', rpc_args))
            else:
                raise TypeError("Invalid type for option 'lifetime'. Expected: ContainerLifetime")
        if _image_pull_policy := kwargs.pop("image_pull_policy", None):
            if _validate_type(_image_pull_policy, ImagePullPolicy):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["pullPolicy"] = typing.cast(ImagePullPolicy, _image_pull_policy)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImagePullPolicy', rpc_args))
            else:
                raise TypeError("Invalid type for option 'image_pull_policy'. Expected: ImagePullPolicy")
        if _publish_as_container := kwargs.pop("publish_as_container", None):
            if _publish_as_container is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/publishAsContainer', rpc_args))
            else:
                raise TypeError("Invalid type for option 'publish_as_container'. Expected: Literal[True]")
        if _dockerfile := kwargs.pop("dockerfile", None):
            if _validate_type(_dockerfile, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["contextPath"] = typing.cast(str, _dockerfile)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDockerfile', rpc_args))
            elif _validate_dict_types(_dockerfile, DockerfileParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["contextPath"] = typing.cast(DockerfileParameters, _dockerfile)["context_path"]
                rpc_args["dockerfilePath"] = typing.cast(DockerfileParameters, _dockerfile).get("dockerfile_path")
                rpc_args["stage"] = typing.cast(DockerfileParameters, _dockerfile).get("stage")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDockerfile', rpc_args))
            else:
                raise TypeError("Invalid type for option 'dockerfile'. Expected: str or DockerfileParameters")
        if _container_name := kwargs.pop("container_name", None):
            if _validate_type(_container_name, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(str, _container_name)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withContainerName', rpc_args))
            else:
                raise TypeError("Invalid type for option 'container_name'. Expected: str")
        if _build_arg := kwargs.pop("build_arg", None):
            if _validate_tuple_types(_build_arg, (str, str | ParameterResource)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(tuple[str, str | ParameterResource], _build_arg)[0]
                rpc_args["value"] = typing.cast(tuple[str, str | ParameterResource], _build_arg)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withBuildArg', rpc_args))
            else:
                raise TypeError("Invalid type for option 'build_arg'. Expected: (str, str | ParameterResource)")
        if _build_secret := kwargs.pop("build_secret", None):
            if _validate_tuple_types(_build_secret, (str, ParameterResource)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(tuple[str, ParameterResource], _build_secret)[0]
                rpc_args["value"] = typing.cast(tuple[str, ParameterResource], _build_secret)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withParameterBuildSecret', rpc_args))
            else:
                raise TypeError("Invalid type for option 'build_secret'. Expected: (str, ParameterResource)")
        if _container_certificate_paths := kwargs.pop("container_certificate_paths", None):
            if _validate_dict_types(_container_certificate_paths, ContainerCertificatePathsParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["customCertificatesDestination"] = typing.cast(ContainerCertificatePathsParameters, _container_certificate_paths).get("custom_certificates_destination")
                rpc_args["defaultCertificateBundlePaths"] = typing.cast(ContainerCertificatePathsParameters, _container_certificate_paths).get("default_certificate_bundle_paths")
                rpc_args["defaultCertificateDirectoryPaths"] = typing.cast(ContainerCertificatePathsParameters, _container_certificate_paths).get("default_certificate_dir_paths")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withContainerCertificatePaths', rpc_args))
            elif _container_certificate_paths is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withContainerCertificatePaths', rpc_args))
            else:
                raise TypeError("Invalid type for option 'container_certificate_paths'. Expected: ContainerCertificatePathsParameters or Literal[True]")
        if _endpoint_proxy_support := kwargs.pop("endpoint_proxy_support", None):
            if _validate_type(_endpoint_proxy_support, bool):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["proxyEnabled"] = typing.cast(bool, _endpoint_proxy_support)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpointProxySupport', rpc_args))
            else:
                raise TypeError("Invalid type for option 'endpoint_proxy_support'. Expected: bool")
        if _dockerfile_builder := kwargs.pop("dockerfile_builder", None):
            if _validate_tuple_types(_dockerfile_builder, (str, typing.Callable[[DockerfileBuilderCallbackContext], None])):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["contextPath"] = typing.cast(tuple[str, typing.Callable[[DockerfileBuilderCallbackContext], None]], _dockerfile_builder)[0]
                rpc_args["callback"] = client.register_callback(typing.cast(tuple[str, typing.Callable[[DockerfileBuilderCallbackContext], None]], _dockerfile_builder)[1])
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDockerfileBuilder', rpc_args))
            elif _validate_dict_types(_dockerfile_builder, DockerfileBuilderParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["contextPath"] = typing.cast(DockerfileBuilderParameters, _dockerfile_builder)["context_path"]
                rpc_args["callback"] = client.register_callback(typing.cast(DockerfileBuilderParameters, _dockerfile_builder)["callback"])
                rpc_args["stage"] = typing.cast(DockerfileBuilderParameters, _dockerfile_builder).get("stage")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDockerfileBuilder', rpc_args))
            else:
                raise TypeError("Invalid type for option 'dockerfile_builder'. Expected: (str, Callable[[DockerfileBuilderCallbackContext], None]) or DockerfileBuilderParameters")
        if _container_network_alias := kwargs.pop("container_network_alias", None):
            if _validate_type(_container_network_alias, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["alias"] = typing.cast(str, _container_network_alias)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withContainerNetworkAlias', rpc_args))
            else:
                raise TypeError("Invalid type for option 'container_network_alias'. Expected: str")
        if _mcp_server := kwargs.pop("mcp_server", None):
            if _validate_dict_types(_mcp_server, McpServerParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(McpServerParameters, _mcp_server).get("path")
                rpc_args["endpointName"] = typing.cast(McpServerParameters, _mcp_server).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withMcpServer', rpc_args))
            elif _mcp_server is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withMcpServer', rpc_args))
            else:
                raise TypeError("Invalid type for option 'mcp_server'. Expected: McpServerParameters or Literal[True]")
        if _otlp_exporter := kwargs.pop("otlp_exporter", None):
            if _validate_type(_otlp_exporter, OtlpProtocol):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["protocol"] = typing.cast(OtlpProtocol, _otlp_exporter)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withOtlpExporter', rpc_args))
            elif _otlp_exporter is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withOtlpExporter', rpc_args))
            else:
                raise TypeError("Invalid type for option 'otlp_exporter'. Expected: OtlpProtocol or Literal[True]")
        if _publish_as_connection_string := kwargs.pop("publish_as_connection_string", None):
            if _publish_as_connection_string is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/publishAsConnectionString', rpc_args))
            else:
                raise TypeError("Invalid type for option 'publish_as_connection_string'. Expected: Literal[True]")
        if _env := kwargs.pop("env", None):
            if _validate_tuple_types(_env, (str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue], _env)[0]
                rpc_args["value"] = typing.cast(tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue], _env)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEnvironment', rpc_args))
            else:
                raise TypeError("Invalid type for option 'env'. Expected: (str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue)")
        if _env_callback := kwargs.pop("env_callback", None):
            if _validate_type(_env_callback, typing.Callable[[EnvironmentCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EnvironmentCallbackContext], None], _env_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEnvironmentCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'env_callback'. Expected: Callable[[EnvironmentCallbackContext], None]")
        if _args := kwargs.pop("args", None):
            if _validate_type(_args, typing.Iterable[str]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["args"] = typing.cast(typing.Iterable[str], _args)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withArgs', rpc_args))
            else:
                raise TypeError("Invalid type for option 'args'. Expected: Iterable[str]")
        if _args_callback := kwargs.pop("args_callback", None):
            if _validate_type(_args_callback, typing.Callable[[CommandLineArgsCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[CommandLineArgsCallbackContext], None], _args_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withArgsCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'args_callback'. Expected: Callable[[CommandLineArgsCallbackContext], None]")
        if _reference_env := kwargs.pop("reference_env", None):
            if _validate_type(_reference_env, ReferenceEnvironmentInjectionOptions):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["options"] = typing.cast(ReferenceEnvironmentInjectionOptions, _reference_env)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReferenceEnvironment', rpc_args))
            else:
                raise TypeError("Invalid type for option 'reference_env'. Expected: ReferenceEnvironmentInjectionOptions")
        if _reference := kwargs.pop("reference", None):
            if _validate_type(_reference, AbstractResource | EndpointReference | str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(AbstractResource | EndpointReference | str, _reference)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReference', rpc_args))
            elif _validate_dict_types(_reference, ReferenceParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(ReferenceParameters, _reference)["source"]
                rpc_args["connectionName"] = typing.cast(ReferenceParameters, _reference).get("connection_name")
                rpc_args["optional"] = typing.cast(ReferenceParameters, _reference).get("optional")
                rpc_args["name"] = typing.cast(ReferenceParameters, _reference).get("name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReference', rpc_args))
            else:
                raise TypeError("Invalid type for option 'reference'. Expected: AbstractResource | EndpointReference | str or ReferenceParameters")
        if _endpoint_callback := kwargs.pop("endpoint_callback", None):
            if _validate_tuple_types(_endpoint_callback, (str, typing.Callable[[EndpointUpdateContext], None])):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(tuple[str, typing.Callable[[EndpointUpdateContext], None]], _endpoint_callback)[0]
                rpc_args["callback"] = client.register_callback(typing.cast(tuple[str, typing.Callable[[EndpointUpdateContext], None]], _endpoint_callback)[1])
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpointCallback', rpc_args))
            elif _validate_dict_types(_endpoint_callback, EndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(EndpointCallbackParameters, _endpoint_callback)["endpoint_name"]
                rpc_args["callback"] = client.register_callback(typing.cast(EndpointCallbackParameters, _endpoint_callback)["callback"])
                rpc_args["createIfNotExists"] = typing.cast(EndpointCallbackParameters, _endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'endpoint_callback'. Expected: (str, Callable[[EndpointUpdateContext], None]) or EndpointCallbackParameters")
        if _http_endpoint_callback := kwargs.pop("http_endpoint_callback", None):
            if _validate_type(_http_endpoint_callback, typing.Callable[[EndpointUpdateContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EndpointUpdateContext], None], _http_endpoint_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpointCallback', rpc_args))
            elif _validate_dict_types(_http_endpoint_callback, HttpEndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback)["callback"])
                rpc_args["name"] = typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback).get("name")
                rpc_args["createIfNotExists"] = typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_endpoint_callback'. Expected: Callable[[EndpointUpdateContext], None] or HttpEndpointCallbackParameters")
        if _https_endpoint_callback := kwargs.pop("https_endpoint_callback", None):
            if _validate_type(_https_endpoint_callback, typing.Callable[[EndpointUpdateContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EndpointUpdateContext], None], _https_endpoint_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpointCallback', rpc_args))
            elif _validate_dict_types(_https_endpoint_callback, HttpsEndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback)["callback"])
                rpc_args["name"] = typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback).get("name")
                rpc_args["createIfNotExists"] = typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_endpoint_callback'. Expected: Callable[[EndpointUpdateContext], None] or HttpsEndpointCallbackParameters")
        if _endpoint := kwargs.pop("endpoint", None):
            if _validate_dict_types(_endpoint, EndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(EndpointParameters, _endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(EndpointParameters, _endpoint).get("target_port")
                rpc_args["scheme"] = typing.cast(EndpointParameters, _endpoint).get("scheme")
                rpc_args["name"] = typing.cast(EndpointParameters, _endpoint).get("name")
                rpc_args["env"] = typing.cast(EndpointParameters, _endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(EndpointParameters, _endpoint).get("is_proxied")
                rpc_args["isExternal"] = typing.cast(EndpointParameters, _endpoint).get("is_external")
                rpc_args["protocol"] = typing.cast(EndpointParameters, _endpoint).get("protocol")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpoint', rpc_args))
            elif _endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'endpoint'. Expected: EndpointParameters or Literal[True]")
        if _http_endpoint := kwargs.pop("http_endpoint", None):
            if _validate_dict_types(_http_endpoint, HttpEndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("target_port")
                rpc_args["name"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("name")
                rpc_args["env"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("is_proxied")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpoint', rpc_args))
            elif _http_endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_endpoint'. Expected: HttpEndpointParameters or Literal[True]")
        if _https_endpoint := kwargs.pop("https_endpoint", None):
            if _validate_dict_types(_https_endpoint, HttpsEndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("target_port")
                rpc_args["name"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("name")
                rpc_args["env"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("is_proxied")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpoint', rpc_args))
            elif _https_endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_endpoint'. Expected: HttpsEndpointParameters or Literal[True]")
        if _external_http_endpoints := kwargs.pop("external_http_endpoints", None):
            if _external_http_endpoints is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withExternalHttpEndpoints', rpc_args))
            else:
                raise TypeError("Invalid type for option 'external_http_endpoints'. Expected: Literal[True]")
        if _as_http2_service := kwargs.pop("as_http2_service", None):
            if _as_http2_service is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/asHttp2Service', rpc_args))
            else:
                raise TypeError("Invalid type for option 'as_http2_service'. Expected: Literal[True]")
        if _wait_for := kwargs.pop("wait_for", None):
            if _validate_type(_wait_for, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitFor', rpc_args))
            elif _validate_tuple_types(_wait_for, (AbstractResource, WaitBehavior)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for)[0]
                rpc_args["waitBehavior"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitFor', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for'. Expected: AbstractResource or (AbstractResource, WaitBehavior)")
        if _wait_for_start := kwargs.pop("wait_for_start", None):
            if _validate_type(_wait_for_start, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for_start)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForStart', rpc_args))
            elif _validate_tuple_types(_wait_for_start, (AbstractResource, WaitBehavior)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for_start)[0]
                rpc_args["waitBehavior"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for_start)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForStart', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for_start'. Expected: AbstractResource or (AbstractResource, WaitBehavior)")
        if _wait_for_completion := kwargs.pop("wait_for_completion", None):
            if _validate_type(_wait_for_completion, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for_completion)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForResourceCompletion', rpc_args))
            elif _validate_tuple_types(_wait_for_completion, (AbstractResource, int)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, int], _wait_for_completion)[0]
                rpc_args["exitCode"] = typing.cast(tuple[AbstractResource, int], _wait_for_completion)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForResourceCompletion', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for_completion'. Expected: AbstractResource or (AbstractResource, int)")
        if _http_health_check := kwargs.pop("http_health_check", None):
            if _validate_dict_types(_http_health_check, HttpHealthCheckParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("path")
                rpc_args["statusCode"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("status_code")
                rpc_args["endpointName"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpHealthCheck', rpc_args))
            elif _http_health_check is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpHealthCheck', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_health_check'. Expected: HttpHealthCheckParameters or Literal[True]")
        if _http_command := kwargs.pop("http_command", None):
            if _validate_tuple_types(_http_command, (str, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(tuple[str, str], _http_command)[0]
                rpc_args["displayName"] = typing.cast(tuple[str, str], _http_command)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpCommand', rpc_args))
            elif _validate_dict_types(_http_command, HttpCommandParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(HttpCommandParameters, _http_command)["path"]
                rpc_args["displayName"] = typing.cast(HttpCommandParameters, _http_command)["display_name"]
                rpc_args["options"] = typing.cast(HttpCommandParameters, _http_command).get("options")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpCommand', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_command'. Expected: (str, str) or HttpCommandParameters")
        if _developer_certificate_trust := kwargs.pop("developer_certificate_trust", None):
            if _validate_type(_developer_certificate_trust, bool):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["trust"] = typing.cast(bool, _developer_certificate_trust)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDeveloperCertificateTrust', rpc_args))
            else:
                raise TypeError("Invalid type for option 'developer_certificate_trust'. Expected: bool")
        if _certificate_trust_scope := kwargs.pop("certificate_trust_scope", None):
            if _validate_type(_certificate_trust_scope, CertificateTrustScope):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["scope"] = typing.cast(CertificateTrustScope, _certificate_trust_scope)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withCertificateTrustScope', rpc_args))
            else:
                raise TypeError("Invalid type for option 'certificate_trust_scope'. Expected: CertificateTrustScope")
        if _https_developer_certificate := kwargs.pop("https_developer_certificate", None):
            if _validate_type(_https_developer_certificate, ParameterResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["password"] = typing.cast(ParameterResource, _https_developer_certificate)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withParameterHttpsDeveloperCertificate', rpc_args))
            elif _https_developer_certificate is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withParameterHttpsDeveloperCertificate', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_developer_certificate'. Expected: ParameterResource or Literal[True]")
        if _without_https_certificate := kwargs.pop("without_https_certificate", None):
            if _without_https_certificate is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withoutHttpsCertificate', rpc_args))
            else:
                raise TypeError("Invalid type for option 'without_https_certificate'. Expected: Literal[True]")
        if _http_probe := kwargs.pop("http_probe", None):
            if _validate_type(_http_probe, ProbeType):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["probeType"] = typing.cast(ProbeType, _http_probe)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpProbe', rpc_args))
            elif _validate_dict_types(_http_probe, HttpProbeParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["probeType"] = typing.cast(HttpProbeParameters, _http_probe)["probe_type"]
                rpc_args["path"] = typing.cast(HttpProbeParameters, _http_probe).get("path")
                rpc_args["initialDelaySeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("initial_delay_seconds")
                rpc_args["periodSeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("period_seconds")
                rpc_args["timeoutSeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("timeout_seconds")
                rpc_args["failureThreshold"] = typing.cast(HttpProbeParameters, _http_probe).get("failure_threshold")
                rpc_args["successThreshold"] = typing.cast(HttpProbeParameters, _http_probe).get("success_threshold")
                rpc_args["endpointName"] = typing.cast(HttpProbeParameters, _http_probe).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpProbe', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_probe'. Expected: ProbeType or HttpProbeParameters")
        if _image_push_options := kwargs.pop("image_push_options", None):
            if _validate_type(_image_push_options, typing.Callable[[ContainerImagePushOptionsCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ContainerImagePushOptionsCallbackContext], None], _image_push_options))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImagePushOptions', rpc_args))
            else:
                raise TypeError("Invalid type for option 'image_push_options'. Expected: Callable[[ContainerImagePushOptionsCallbackContext], None]")
        if _remote_image_name := kwargs.pop("remote_image_name", None):
            if _validate_type(_remote_image_name, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["remoteImageName"] = typing.cast(str, _remote_image_name)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withRemoteImageName', rpc_args))
            else:
                raise TypeError("Invalid type for option 'remote_image_name'. Expected: str")
        if _remote_image_tag := kwargs.pop("remote_image_tag", None):
            if _validate_type(_remote_image_tag, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["remoteImageTag"] = typing.cast(str, _remote_image_tag)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withRemoteImageTag', rpc_args))
            else:
                raise TypeError("Invalid type for option 'remote_image_tag'. Expected: str")
        if _volume := kwargs.pop("volume", None):
            if _validate_type(_volume, str):
                rpc_args: dict[str, typing.Any] = {"resource": handle}
                rpc_args["target"] = typing.cast(str, _volume)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withVolume', rpc_args))
            elif _validate_dict_types(_volume, VolumeParameters):
                rpc_args: dict[str, typing.Any] = {"resource": handle}
                rpc_args["target"] = typing.cast(VolumeParameters, _volume)["target"]
                rpc_args["name"] = typing.cast(VolumeParameters, _volume).get("name")
                rpc_args["isReadOnly"] = typing.cast(VolumeParameters, _volume).get("is_read_only")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withVolume', rpc_args))
            else:
                raise TypeError("Invalid type for option 'volume'. Expected: str or VolumeParameters")
        if _on_resource_endpoints_allocated := kwargs.pop("on_resource_endpoints_allocated", None):
            if _validate_type(_on_resource_endpoints_allocated, typing.Callable[[ResourceEndpointsAllocatedEvent], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ResourceEndpointsAllocatedEvent], None], _on_resource_endpoints_allocated))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/onResourceEndpointsAllocated', rpc_args))
            else:
                raise TypeError("Invalid type for option 'on_resource_endpoints_allocated'. Expected: Callable[[ResourceEndpointsAllocatedEvent], None]")
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


class ProjectResourceKwargs(_BaseResourceKwargs, total=False):
    """ProjectResource options."""

    mcp_server: McpServerParameters | typing.Literal[True]
    otlp_exporter: OtlpProtocol | typing.Literal[True]
    replicas: int
    disable_forwarded_headers: typing.Literal[True]
    publish_as_docker_file: typing.Callable[[ContainerResource], None] | typing.Literal[True]
    env: tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue]
    env_callback: typing.Callable[[EnvironmentCallbackContext], None]
    args: typing.Iterable[str]
    args_callback: typing.Callable[[CommandLineArgsCallbackContext], None]
    reference_env: ReferenceEnvironmentInjectionOptions
    reference: AbstractResource | EndpointReference | str | ReferenceParameters
    endpoint_callback: tuple[str, typing.Callable[[EndpointUpdateContext], None]] | EndpointCallbackParameters
    http_endpoint_callback: typing.Callable[[EndpointUpdateContext], None] | HttpEndpointCallbackParameters
    https_endpoint_callback: typing.Callable[[EndpointUpdateContext], None] | HttpsEndpointCallbackParameters
    endpoint: EndpointParameters | typing.Literal[True]
    http_endpoint: HttpEndpointParameters | typing.Literal[True]
    https_endpoint: HttpsEndpointParameters | typing.Literal[True]
    external_http_endpoints: typing.Literal[True]
    as_http2_service: typing.Literal[True]
    publish_with_container_files: tuple[AbstractResourceWithContainerFiles, str]
    wait_for: AbstractResource | tuple[AbstractResource, WaitBehavior]
    wait_for_start: AbstractResource | tuple[AbstractResource, WaitBehavior]
    wait_for_completion: AbstractResource | tuple[AbstractResource, int]
    http_health_check: HttpHealthCheckParameters | typing.Literal[True]
    http_command: tuple[str, str] | HttpCommandParameters
    developer_certificate_trust: bool
    certificate_trust_scope: CertificateTrustScope
    https_developer_certificate: ParameterResource | typing.Literal[True]
    without_https_certificate: typing.Literal[True]
    http_probe: ProbeType | HttpProbeParameters
    image_push_options: typing.Callable[[ContainerImagePushOptionsCallbackContext], None]
    remote_image_name: str
    remote_image_tag: str
    on_resource_endpoints_allocated: typing.Callable[[ResourceEndpointsAllocatedEvent], None]
    test_with_env_callback: typing.Callable[[TestEnvironmentContext], None]
    env_vars: typing.Mapping[str, str]

class ProjectResource(_BaseResource, AbstractResourceWithEnvironment, AbstractResourceWithArgs, AbstractResourceWithServiceDiscovery, AbstractResourceWithWaitSupport, AbstractResourceWithProbes, AbstractComputeResource, AbstractContainerFilesDestinationResource):
    """ProjectResource resource."""

    def __repr__(self) -> str:
        return "ProjectResource(handle={self._handle.handle_id})"

    def with_mcp_server(self, *, path: str = "/mcp", endpoint_name: str | None = None) -> typing.Self:
        """Configures an MCP server endpoint on the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if path is not None:
            rpc_args['path'] = path
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withMcpServer',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_otlp_exporter(self, *, protocol: OtlpProtocol | None = None) -> typing.Self:
        """Configures OTLP telemetry export"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if protocol is not None:
            rpc_args['protocol'] = protocol
        result = self._client.invoke_capability(
            'Aspire.Hosting/withOtlpExporter',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_replicas(self, replicas: int) -> typing.Self:
        """Sets the number of replicas"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['replicas'] = replicas
        result = self._client.invoke_capability(
            'Aspire.Hosting/withReplicas',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def disable_forwarded_headers(self) -> typing.Self:
        """Disables forwarded headers for the project"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/disableForwardedHeaders',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def publish_as_docker_file(self, *, configure: typing.Callable[[ContainerResource], None] | None = None) -> typing.Self:
        """Publishes a project as a Docker file with optional container configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if configure is not None:
            rpc_args['configure'] = self._client.register_callback(configure)
        result = self._client.invoke_capability(
            'Aspire.Hosting/publishProjectAsDockerFileWithConfigure',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_env(self, name: str, value: str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue) -> typing.Self:
        """Sets an environment variable"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEnvironment',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_env_callback(self, callback: typing.Callable[[EnvironmentCallbackContext], None]) -> typing.Self:
        """Sets environment variables via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEnvironmentCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_args(self, args: typing.Iterable[str]) -> typing.Self:
        """Adds arguments"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['args'] = args
        result = self._client.invoke_capability(
            'Aspire.Hosting/withArgs',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_args_callback(self, callback: typing.Callable[[CommandLineArgsCallbackContext], None]) -> typing.Self:
        """Sets command-line arguments via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withArgsCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_reference_env(self, options: ReferenceEnvironmentInjectionOptions) -> typing.Self:
        """Configures which reference values are injected into environment variables"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['options'] = options
        result = self._client.invoke_capability(
            'Aspire.Hosting/withReferenceEnvironment',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_reference(self, source: AbstractResource | EndpointReference | str, *, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> typing.Self:
        """Adds a reference to another resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['source'] = source
        if connection_name is not None:
            rpc_args['connectionName'] = connection_name
        if optional is not None:
            rpc_args['optional'] = optional
        if name is not None:
            rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withReference',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_endpoint_callback(self, endpoint_name: str, callback: typing.Callable[[EndpointUpdateContext], None], *, create_if_not_exists: bool = True) -> typing.Self:
        """Updates a named endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['endpointName'] = endpoint_name
        rpc_args['callback'] = self._client.register_callback(callback)
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_endpoint_callback(self, callback: typing.Callable[[EndpointUpdateContext], None], *, name: str | None = None, create_if_not_exists: bool = True) -> typing.Self:
        """Updates an HTTP endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        if name is not None:
            rpc_args['name'] = name
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_endpoint_callback(self, callback: typing.Callable[[EndpointUpdateContext], None], *, name: str | None = None, create_if_not_exists: bool = True) -> typing.Self:
        """Updates an HTTPS endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        if name is not None:
            rpc_args['name'] = name
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpsEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_endpoint(self, *, port: int | None = None, target_port: int | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> typing.Self:
        """Adds a network endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if scheme is not None:
            rpc_args['scheme'] = scheme
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        if is_external is not None:
            rpc_args['isExternal'] = is_external
        if protocol is not None:
            rpc_args['protocol'] = protocol
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_endpoint(self, *, port: int | None = None, target_port: int | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> typing.Self:
        """Adds an HTTP endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_endpoint(self, *, port: int | None = None, target_port: int | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> typing.Self:
        """Adds an HTTPS endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpsEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_external_http_endpoints(self) -> typing.Self:
        """Makes HTTP endpoints externally accessible"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withExternalHttpEndpoints',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/getEndpoint',
            rpc_args,
        )
        return typing.cast(EndpointReference, result)

    def as_http2_service(self) -> typing.Self:
        """Configures resource for HTTP/2"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/asHttp2Service',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def publish_with_container_files(self, source: AbstractResourceWithContainerFiles, destination_path: str) -> typing.Self:
        """Configures the resource to copy container files from the specified source during publishing"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['source'] = source
        rpc_args['destinationPath'] = destination_path
        result = self._client.invoke_capability(
            'Aspire.Hosting/publishWithContainerFilesFromResource',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for(self, dependency: AbstractResource, *, wait_behavior: WaitBehavior | None = None) -> typing.Self:
        """Waits for another resource to be ready"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if wait_behavior is not None:
            rpc_args['waitBehavior'] = wait_behavior
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitFor',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for_start(self, dependency: AbstractResource, *, wait_behavior: WaitBehavior | None = None) -> typing.Self:
        """Waits for another resource to start"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if wait_behavior is not None:
            rpc_args['waitBehavior'] = wait_behavior
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitForStart',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for_completion(self, dependency: AbstractResource, *, exit_code: int = 0) -> typing.Self:
        """Waits for resource completion"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if exit_code is not None:
            rpc_args['exitCode'] = exit_code
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitForResourceCompletion',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_health_check(self, *, path: str | None = None, status_code: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health check"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if path is not None:
            rpc_args['path'] = path
        if status_code is not None:
            rpc_args['statusCode'] = status_code
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpHealthCheck',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_command(self, path: str, display_name: str, *, options: HttpCommandExportOptions | None = None) -> typing.Self:
        """Adds an HTTP resource command"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['path'] = path
        rpc_args['displayName'] = display_name
        if options is not None:
            rpc_args['options'] = options
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpCommand',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_developer_certificate_trust(self, trust: bool) -> typing.Self:
        """Configures developer certificate trust"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['trust'] = trust
        result = self._client.invoke_capability(
            'Aspire.Hosting/withDeveloperCertificateTrust',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> typing.Self:
        """Sets the certificate trust scope"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['scope'] = scope
        result = self._client.invoke_capability(
            'Aspire.Hosting/withCertificateTrustScope',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_developer_certificate(self, *, password: ParameterResource | None = None) -> typing.Self:
        """Configures HTTPS with a developer certificate"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if password is not None:
            rpc_args['password'] = password
        result = self._client.invoke_capability(
            'Aspire.Hosting/withParameterHttpsDeveloperCertificate',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def without_https_certificate(self) -> typing.Self:
        """Removes HTTPS certificate configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withoutHttpsCertificate',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_probe(self, probe_type: ProbeType, *, path: str | None = None, initial_delay_seconds: int | None = None, period_seconds: int | None = None, timeout_seconds: int | None = None, failure_threshold: int | None = None, success_threshold: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health probe to the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['probeType'] = probe_type
        if path is not None:
            rpc_args['path'] = path
        if initial_delay_seconds is not None:
            rpc_args['initialDelaySeconds'] = initial_delay_seconds
        if period_seconds is not None:
            rpc_args['periodSeconds'] = period_seconds
        if timeout_seconds is not None:
            rpc_args['timeoutSeconds'] = timeout_seconds
        if failure_threshold is not None:
            rpc_args['failureThreshold'] = failure_threshold
        if success_threshold is not None:
            rpc_args['successThreshold'] = success_threshold
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpProbe',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_image_push_options(self, callback: typing.Callable[[ContainerImagePushOptionsCallbackContext], None]) -> typing.Self:
        """Sets image push options via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withImagePushOptions',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_remote_image_name(self, remote_image_name: str) -> typing.Self:
        """Sets the remote image name for publishing"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['remoteImageName'] = remote_image_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withRemoteImageName',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_remote_image_tag(self, remote_image_tag: str) -> typing.Self:
        """Sets the remote image tag for publishing"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['remoteImageTag'] = remote_image_tag
        result = self._client.invoke_capability(
            'Aspire.Hosting/withRemoteImageTag',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def on_resource_endpoints_allocated(self, callback: typing.Callable[[ResourceEndpointsAllocatedEvent], None]) -> typing.Self:
        """Subscribes to the ResourceEndpointsAllocated event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/onResourceEndpointsAllocated',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

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

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[ProjectResourceKwargs]) -> None:
        if _mcp_server := kwargs.pop("mcp_server", None):
            if _validate_dict_types(_mcp_server, McpServerParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(McpServerParameters, _mcp_server).get("path")
                rpc_args["endpointName"] = typing.cast(McpServerParameters, _mcp_server).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withMcpServer', rpc_args))
            elif _mcp_server is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withMcpServer', rpc_args))
            else:
                raise TypeError("Invalid type for option 'mcp_server'. Expected: McpServerParameters or Literal[True]")
        if _otlp_exporter := kwargs.pop("otlp_exporter", None):
            if _validate_type(_otlp_exporter, OtlpProtocol):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["protocol"] = typing.cast(OtlpProtocol, _otlp_exporter)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withOtlpExporter', rpc_args))
            elif _otlp_exporter is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withOtlpExporter', rpc_args))
            else:
                raise TypeError("Invalid type for option 'otlp_exporter'. Expected: OtlpProtocol or Literal[True]")
        if _replicas := kwargs.pop("replicas", None):
            if _validate_type(_replicas, int):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["replicas"] = typing.cast(int, _replicas)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReplicas', rpc_args))
            else:
                raise TypeError("Invalid type for option 'replicas'. Expected: int")
        if _disable_forwarded_headers := kwargs.pop("disable_forwarded_headers", None):
            if _disable_forwarded_headers is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/disableForwardedHeaders', rpc_args))
            else:
                raise TypeError("Invalid type for option 'disable_forwarded_headers'. Expected: Literal[True]")
        if _publish_as_docker_file := kwargs.pop("publish_as_docker_file", None):
            if _validate_type(_publish_as_docker_file, typing.Callable[[ContainerResource], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["configure"] = client.register_callback(typing.cast(typing.Callable[[ContainerResource], None], _publish_as_docker_file))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/publishProjectAsDockerFileWithConfigure', rpc_args))
            elif _publish_as_docker_file is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/publishProjectAsDockerFileWithConfigure', rpc_args))
            else:
                raise TypeError("Invalid type for option 'publish_as_docker_file'. Expected: Callable[[ContainerResource], None] or Literal[True]")
        if _env := kwargs.pop("env", None):
            if _validate_tuple_types(_env, (str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue], _env)[0]
                rpc_args["value"] = typing.cast(tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue], _env)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEnvironment', rpc_args))
            else:
                raise TypeError("Invalid type for option 'env'. Expected: (str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue)")
        if _env_callback := kwargs.pop("env_callback", None):
            if _validate_type(_env_callback, typing.Callable[[EnvironmentCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EnvironmentCallbackContext], None], _env_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEnvironmentCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'env_callback'. Expected: Callable[[EnvironmentCallbackContext], None]")
        if _args := kwargs.pop("args", None):
            if _validate_type(_args, typing.Iterable[str]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["args"] = typing.cast(typing.Iterable[str], _args)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withArgs', rpc_args))
            else:
                raise TypeError("Invalid type for option 'args'. Expected: Iterable[str]")
        if _args_callback := kwargs.pop("args_callback", None):
            if _validate_type(_args_callback, typing.Callable[[CommandLineArgsCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[CommandLineArgsCallbackContext], None], _args_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withArgsCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'args_callback'. Expected: Callable[[CommandLineArgsCallbackContext], None]")
        if _reference_env := kwargs.pop("reference_env", None):
            if _validate_type(_reference_env, ReferenceEnvironmentInjectionOptions):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["options"] = typing.cast(ReferenceEnvironmentInjectionOptions, _reference_env)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReferenceEnvironment', rpc_args))
            else:
                raise TypeError("Invalid type for option 'reference_env'. Expected: ReferenceEnvironmentInjectionOptions")
        if _reference := kwargs.pop("reference", None):
            if _validate_type(_reference, AbstractResource | EndpointReference | str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(AbstractResource | EndpointReference | str, _reference)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReference', rpc_args))
            elif _validate_dict_types(_reference, ReferenceParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(ReferenceParameters, _reference)["source"]
                rpc_args["connectionName"] = typing.cast(ReferenceParameters, _reference).get("connection_name")
                rpc_args["optional"] = typing.cast(ReferenceParameters, _reference).get("optional")
                rpc_args["name"] = typing.cast(ReferenceParameters, _reference).get("name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReference', rpc_args))
            else:
                raise TypeError("Invalid type for option 'reference'. Expected: AbstractResource | EndpointReference | str or ReferenceParameters")
        if _endpoint_callback := kwargs.pop("endpoint_callback", None):
            if _validate_tuple_types(_endpoint_callback, (str, typing.Callable[[EndpointUpdateContext], None])):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(tuple[str, typing.Callable[[EndpointUpdateContext], None]], _endpoint_callback)[0]
                rpc_args["callback"] = client.register_callback(typing.cast(tuple[str, typing.Callable[[EndpointUpdateContext], None]], _endpoint_callback)[1])
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpointCallback', rpc_args))
            elif _validate_dict_types(_endpoint_callback, EndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(EndpointCallbackParameters, _endpoint_callback)["endpoint_name"]
                rpc_args["callback"] = client.register_callback(typing.cast(EndpointCallbackParameters, _endpoint_callback)["callback"])
                rpc_args["createIfNotExists"] = typing.cast(EndpointCallbackParameters, _endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'endpoint_callback'. Expected: (str, Callable[[EndpointUpdateContext], None]) or EndpointCallbackParameters")
        if _http_endpoint_callback := kwargs.pop("http_endpoint_callback", None):
            if _validate_type(_http_endpoint_callback, typing.Callable[[EndpointUpdateContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EndpointUpdateContext], None], _http_endpoint_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpointCallback', rpc_args))
            elif _validate_dict_types(_http_endpoint_callback, HttpEndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback)["callback"])
                rpc_args["name"] = typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback).get("name")
                rpc_args["createIfNotExists"] = typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_endpoint_callback'. Expected: Callable[[EndpointUpdateContext], None] or HttpEndpointCallbackParameters")
        if _https_endpoint_callback := kwargs.pop("https_endpoint_callback", None):
            if _validate_type(_https_endpoint_callback, typing.Callable[[EndpointUpdateContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EndpointUpdateContext], None], _https_endpoint_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpointCallback', rpc_args))
            elif _validate_dict_types(_https_endpoint_callback, HttpsEndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback)["callback"])
                rpc_args["name"] = typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback).get("name")
                rpc_args["createIfNotExists"] = typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_endpoint_callback'. Expected: Callable[[EndpointUpdateContext], None] or HttpsEndpointCallbackParameters")
        if _endpoint := kwargs.pop("endpoint", None):
            if _validate_dict_types(_endpoint, EndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(EndpointParameters, _endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(EndpointParameters, _endpoint).get("target_port")
                rpc_args["scheme"] = typing.cast(EndpointParameters, _endpoint).get("scheme")
                rpc_args["name"] = typing.cast(EndpointParameters, _endpoint).get("name")
                rpc_args["env"] = typing.cast(EndpointParameters, _endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(EndpointParameters, _endpoint).get("is_proxied")
                rpc_args["isExternal"] = typing.cast(EndpointParameters, _endpoint).get("is_external")
                rpc_args["protocol"] = typing.cast(EndpointParameters, _endpoint).get("protocol")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpoint', rpc_args))
            elif _endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'endpoint'. Expected: EndpointParameters or Literal[True]")
        if _http_endpoint := kwargs.pop("http_endpoint", None):
            if _validate_dict_types(_http_endpoint, HttpEndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("target_port")
                rpc_args["name"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("name")
                rpc_args["env"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("is_proxied")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpoint', rpc_args))
            elif _http_endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_endpoint'. Expected: HttpEndpointParameters or Literal[True]")
        if _https_endpoint := kwargs.pop("https_endpoint", None):
            if _validate_dict_types(_https_endpoint, HttpsEndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("target_port")
                rpc_args["name"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("name")
                rpc_args["env"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("is_proxied")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpoint', rpc_args))
            elif _https_endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_endpoint'. Expected: HttpsEndpointParameters or Literal[True]")
        if _external_http_endpoints := kwargs.pop("external_http_endpoints", None):
            if _external_http_endpoints is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withExternalHttpEndpoints', rpc_args))
            else:
                raise TypeError("Invalid type for option 'external_http_endpoints'. Expected: Literal[True]")
        if _as_http2_service := kwargs.pop("as_http2_service", None):
            if _as_http2_service is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/asHttp2Service', rpc_args))
            else:
                raise TypeError("Invalid type for option 'as_http2_service'. Expected: Literal[True]")
        if _publish_with_container_files := kwargs.pop("publish_with_container_files", None):
            if _validate_tuple_types(_publish_with_container_files, (AbstractResourceWithContainerFiles, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(tuple[AbstractResourceWithContainerFiles, str], _publish_with_container_files)[0]
                rpc_args["destinationPath"] = typing.cast(tuple[AbstractResourceWithContainerFiles, str], _publish_with_container_files)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/publishWithContainerFilesFromResource', rpc_args))
            else:
                raise TypeError("Invalid type for option 'publish_with_container_files'. Expected: (AbstractResourceWithContainerFiles, str)")
        if _wait_for := kwargs.pop("wait_for", None):
            if _validate_type(_wait_for, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitFor', rpc_args))
            elif _validate_tuple_types(_wait_for, (AbstractResource, WaitBehavior)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for)[0]
                rpc_args["waitBehavior"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitFor', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for'. Expected: AbstractResource or (AbstractResource, WaitBehavior)")
        if _wait_for_start := kwargs.pop("wait_for_start", None):
            if _validate_type(_wait_for_start, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for_start)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForStart', rpc_args))
            elif _validate_tuple_types(_wait_for_start, (AbstractResource, WaitBehavior)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for_start)[0]
                rpc_args["waitBehavior"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for_start)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForStart', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for_start'. Expected: AbstractResource or (AbstractResource, WaitBehavior)")
        if _wait_for_completion := kwargs.pop("wait_for_completion", None):
            if _validate_type(_wait_for_completion, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for_completion)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForResourceCompletion', rpc_args))
            elif _validate_tuple_types(_wait_for_completion, (AbstractResource, int)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, int], _wait_for_completion)[0]
                rpc_args["exitCode"] = typing.cast(tuple[AbstractResource, int], _wait_for_completion)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForResourceCompletion', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for_completion'. Expected: AbstractResource or (AbstractResource, int)")
        if _http_health_check := kwargs.pop("http_health_check", None):
            if _validate_dict_types(_http_health_check, HttpHealthCheckParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("path")
                rpc_args["statusCode"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("status_code")
                rpc_args["endpointName"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpHealthCheck', rpc_args))
            elif _http_health_check is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpHealthCheck', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_health_check'. Expected: HttpHealthCheckParameters or Literal[True]")
        if _http_command := kwargs.pop("http_command", None):
            if _validate_tuple_types(_http_command, (str, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(tuple[str, str], _http_command)[0]
                rpc_args["displayName"] = typing.cast(tuple[str, str], _http_command)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpCommand', rpc_args))
            elif _validate_dict_types(_http_command, HttpCommandParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(HttpCommandParameters, _http_command)["path"]
                rpc_args["displayName"] = typing.cast(HttpCommandParameters, _http_command)["display_name"]
                rpc_args["options"] = typing.cast(HttpCommandParameters, _http_command).get("options")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpCommand', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_command'. Expected: (str, str) or HttpCommandParameters")
        if _developer_certificate_trust := kwargs.pop("developer_certificate_trust", None):
            if _validate_type(_developer_certificate_trust, bool):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["trust"] = typing.cast(bool, _developer_certificate_trust)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDeveloperCertificateTrust', rpc_args))
            else:
                raise TypeError("Invalid type for option 'developer_certificate_trust'. Expected: bool")
        if _certificate_trust_scope := kwargs.pop("certificate_trust_scope", None):
            if _validate_type(_certificate_trust_scope, CertificateTrustScope):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["scope"] = typing.cast(CertificateTrustScope, _certificate_trust_scope)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withCertificateTrustScope', rpc_args))
            else:
                raise TypeError("Invalid type for option 'certificate_trust_scope'. Expected: CertificateTrustScope")
        if _https_developer_certificate := kwargs.pop("https_developer_certificate", None):
            if _validate_type(_https_developer_certificate, ParameterResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["password"] = typing.cast(ParameterResource, _https_developer_certificate)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withParameterHttpsDeveloperCertificate', rpc_args))
            elif _https_developer_certificate is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withParameterHttpsDeveloperCertificate', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_developer_certificate'. Expected: ParameterResource or Literal[True]")
        if _without_https_certificate := kwargs.pop("without_https_certificate", None):
            if _without_https_certificate is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withoutHttpsCertificate', rpc_args))
            else:
                raise TypeError("Invalid type for option 'without_https_certificate'. Expected: Literal[True]")
        if _http_probe := kwargs.pop("http_probe", None):
            if _validate_type(_http_probe, ProbeType):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["probeType"] = typing.cast(ProbeType, _http_probe)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpProbe', rpc_args))
            elif _validate_dict_types(_http_probe, HttpProbeParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["probeType"] = typing.cast(HttpProbeParameters, _http_probe)["probe_type"]
                rpc_args["path"] = typing.cast(HttpProbeParameters, _http_probe).get("path")
                rpc_args["initialDelaySeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("initial_delay_seconds")
                rpc_args["periodSeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("period_seconds")
                rpc_args["timeoutSeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("timeout_seconds")
                rpc_args["failureThreshold"] = typing.cast(HttpProbeParameters, _http_probe).get("failure_threshold")
                rpc_args["successThreshold"] = typing.cast(HttpProbeParameters, _http_probe).get("success_threshold")
                rpc_args["endpointName"] = typing.cast(HttpProbeParameters, _http_probe).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpProbe', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_probe'. Expected: ProbeType or HttpProbeParameters")
        if _image_push_options := kwargs.pop("image_push_options", None):
            if _validate_type(_image_push_options, typing.Callable[[ContainerImagePushOptionsCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ContainerImagePushOptionsCallbackContext], None], _image_push_options))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImagePushOptions', rpc_args))
            else:
                raise TypeError("Invalid type for option 'image_push_options'. Expected: Callable[[ContainerImagePushOptionsCallbackContext], None]")
        if _remote_image_name := kwargs.pop("remote_image_name", None):
            if _validate_type(_remote_image_name, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["remoteImageName"] = typing.cast(str, _remote_image_name)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withRemoteImageName', rpc_args))
            else:
                raise TypeError("Invalid type for option 'remote_image_name'. Expected: str")
        if _remote_image_tag := kwargs.pop("remote_image_tag", None):
            if _validate_type(_remote_image_tag, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["remoteImageTag"] = typing.cast(str, _remote_image_tag)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withRemoteImageTag', rpc_args))
            else:
                raise TypeError("Invalid type for option 'remote_image_tag'. Expected: str")
        if _on_resource_endpoints_allocated := kwargs.pop("on_resource_endpoints_allocated", None):
            if _validate_type(_on_resource_endpoints_allocated, typing.Callable[[ResourceEndpointsAllocatedEvent], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ResourceEndpointsAllocatedEvent], None], _on_resource_endpoints_allocated))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/onResourceEndpointsAllocated', rpc_args))
            else:
                raise TypeError("Invalid type for option 'on_resource_endpoints_allocated'. Expected: Callable[[ResourceEndpointsAllocatedEvent], None]")
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


class CSharpAppResourceKwargs(ProjectResourceKwargs, total=False):
    """CSharpAppResource options."""


class CSharpAppResource(ProjectResource):
    """CSharpAppResource resource."""

    def __repr__(self) -> str:
        return "CSharpAppResource(handle={self._handle.handle_id})"

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[CSharpAppResourceKwargs]) -> None:
        super().__init__(handle, client, **kwargs)


class ExecutableResourceKwargs(_BaseResourceKwargs, total=False):
    """ExecutableResource options."""

    publish_as_docker_file: typing.Callable[[ContainerResource], None]
    executable_command: str
    working_dir: str
    mcp_server: McpServerParameters | typing.Literal[True]
    otlp_exporter: OtlpProtocol | typing.Literal[True]
    env: tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue]
    env_callback: typing.Callable[[EnvironmentCallbackContext], None]
    args: typing.Iterable[str]
    args_callback: typing.Callable[[CommandLineArgsCallbackContext], None]
    reference_env: ReferenceEnvironmentInjectionOptions
    reference: AbstractResource | EndpointReference | str | ReferenceParameters
    endpoint_callback: tuple[str, typing.Callable[[EndpointUpdateContext], None]] | EndpointCallbackParameters
    http_endpoint_callback: typing.Callable[[EndpointUpdateContext], None] | HttpEndpointCallbackParameters
    https_endpoint_callback: typing.Callable[[EndpointUpdateContext], None] | HttpsEndpointCallbackParameters
    endpoint: EndpointParameters | typing.Literal[True]
    http_endpoint: HttpEndpointParameters | typing.Literal[True]
    https_endpoint: HttpsEndpointParameters | typing.Literal[True]
    external_http_endpoints: typing.Literal[True]
    as_http2_service: typing.Literal[True]
    wait_for: AbstractResource | tuple[AbstractResource, WaitBehavior]
    wait_for_start: AbstractResource | tuple[AbstractResource, WaitBehavior]
    wait_for_completion: AbstractResource | tuple[AbstractResource, int]
    http_health_check: HttpHealthCheckParameters | typing.Literal[True]
    http_command: tuple[str, str] | HttpCommandParameters
    developer_certificate_trust: bool
    certificate_trust_scope: CertificateTrustScope
    https_developer_certificate: ParameterResource | typing.Literal[True]
    without_https_certificate: typing.Literal[True]
    http_probe: ProbeType | HttpProbeParameters
    image_push_options: typing.Callable[[ContainerImagePushOptionsCallbackContext], None]
    remote_image_name: str
    remote_image_tag: str
    on_resource_endpoints_allocated: typing.Callable[[ResourceEndpointsAllocatedEvent], None]
    test_with_env_callback: typing.Callable[[TestEnvironmentContext], None]
    env_vars: typing.Mapping[str, str]

class ExecutableResource(_BaseResource, AbstractResourceWithEnvironment, AbstractResourceWithArgs, AbstractResourceWithEndpoints, AbstractResourceWithWaitSupport, AbstractResourceWithProbes, AbstractComputeResource):
    """ExecutableResource resource."""

    def __repr__(self) -> str:
        return "ExecutableResource(handle={self._handle.handle_id})"

    def publish_as_docker_file(self, configure: typing.Callable[[ContainerResource], None]) -> typing.Self:
        """Publishes an executable as a Docker file"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['configure'] = self._client.register_callback(configure)
        result = self._client.invoke_capability(
            'Aspire.Hosting/publishAsDockerFile',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_executable_command(self, command: str) -> typing.Self:
        """Sets the executable command"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['command'] = command
        result = self._client.invoke_capability(
            'Aspire.Hosting/withExecutableCommand',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_working_dir(self, working_dir: str) -> typing.Self:
        """Sets the executable working directory"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['workingDirectory'] = working_dir
        result = self._client.invoke_capability(
            'Aspire.Hosting/withWorkingDirectory',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_mcp_server(self, *, path: str = "/mcp", endpoint_name: str | None = None) -> typing.Self:
        """Configures an MCP server endpoint on the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if path is not None:
            rpc_args['path'] = path
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withMcpServer',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_otlp_exporter(self, *, protocol: OtlpProtocol | None = None) -> typing.Self:
        """Configures OTLP telemetry export"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if protocol is not None:
            rpc_args['protocol'] = protocol
        result = self._client.invoke_capability(
            'Aspire.Hosting/withOtlpExporter',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_env(self, name: str, value: str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue) -> typing.Self:
        """Sets an environment variable"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEnvironment',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_env_callback(self, callback: typing.Callable[[EnvironmentCallbackContext], None]) -> typing.Self:
        """Sets environment variables via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEnvironmentCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_args(self, args: typing.Iterable[str]) -> typing.Self:
        """Adds arguments"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['args'] = args
        result = self._client.invoke_capability(
            'Aspire.Hosting/withArgs',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_args_callback(self, callback: typing.Callable[[CommandLineArgsCallbackContext], None]) -> typing.Self:
        """Sets command-line arguments via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withArgsCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_reference_env(self, options: ReferenceEnvironmentInjectionOptions) -> typing.Self:
        """Configures which reference values are injected into environment variables"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['options'] = options
        result = self._client.invoke_capability(
            'Aspire.Hosting/withReferenceEnvironment',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_reference(self, source: AbstractResource | EndpointReference | str, *, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> typing.Self:
        """Adds a reference to another resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['source'] = source
        if connection_name is not None:
            rpc_args['connectionName'] = connection_name
        if optional is not None:
            rpc_args['optional'] = optional
        if name is not None:
            rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withReference',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_endpoint_callback(self, endpoint_name: str, callback: typing.Callable[[EndpointUpdateContext], None], *, create_if_not_exists: bool = True) -> typing.Self:
        """Updates a named endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['endpointName'] = endpoint_name
        rpc_args['callback'] = self._client.register_callback(callback)
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_endpoint_callback(self, callback: typing.Callable[[EndpointUpdateContext], None], *, name: str | None = None, create_if_not_exists: bool = True) -> typing.Self:
        """Updates an HTTP endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        if name is not None:
            rpc_args['name'] = name
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_endpoint_callback(self, callback: typing.Callable[[EndpointUpdateContext], None], *, name: str | None = None, create_if_not_exists: bool = True) -> typing.Self:
        """Updates an HTTPS endpoint via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        if name is not None:
            rpc_args['name'] = name
        if create_if_not_exists is not None:
            rpc_args['createIfNotExists'] = create_if_not_exists
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpsEndpointCallback',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_endpoint(self, *, port: int | None = None, target_port: int | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> typing.Self:
        """Adds a network endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if scheme is not None:
            rpc_args['scheme'] = scheme
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        if is_external is not None:
            rpc_args['isExternal'] = is_external
        if protocol is not None:
            rpc_args['protocol'] = protocol
        result = self._client.invoke_capability(
            'Aspire.Hosting/withEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_endpoint(self, *, port: int | None = None, target_port: int | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> typing.Self:
        """Adds an HTTP endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_endpoint(self, *, port: int | None = None, target_port: int | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> typing.Self:
        """Adds an HTTPS endpoint"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if port is not None:
            rpc_args['port'] = port
        if target_port is not None:
            rpc_args['targetPort'] = target_port
        if name is not None:
            rpc_args['name'] = name
        if env is not None:
            rpc_args['env'] = env
        if is_proxied is not None:
            rpc_args['isProxied'] = is_proxied
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpsEndpoint',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_external_http_endpoints(self) -> typing.Self:
        """Makes HTTP endpoints externally accessible"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withExternalHttpEndpoints',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        result = self._client.invoke_capability(
            'Aspire.Hosting/getEndpoint',
            rpc_args,
        )
        return typing.cast(EndpointReference, result)

    def as_http2_service(self) -> typing.Self:
        """Configures resource for HTTP/2"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/asHttp2Service',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for(self, dependency: AbstractResource, *, wait_behavior: WaitBehavior | None = None) -> typing.Self:
        """Waits for another resource to be ready"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if wait_behavior is not None:
            rpc_args['waitBehavior'] = wait_behavior
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitFor',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for_start(self, dependency: AbstractResource, *, wait_behavior: WaitBehavior | None = None) -> typing.Self:
        """Waits for another resource to start"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if wait_behavior is not None:
            rpc_args['waitBehavior'] = wait_behavior
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitForStart',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def wait_for_completion(self, dependency: AbstractResource, *, exit_code: int = 0) -> typing.Self:
        """Waits for resource completion"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['dependency'] = dependency
        if exit_code is not None:
            rpc_args['exitCode'] = exit_code
        result = self._client.invoke_capability(
            'Aspire.Hosting/waitForResourceCompletion',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_health_check(self, *, path: str | None = None, status_code: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health check"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if path is not None:
            rpc_args['path'] = path
        if status_code is not None:
            rpc_args['statusCode'] = status_code
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpHealthCheck',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_command(self, path: str, display_name: str, *, options: HttpCommandExportOptions | None = None) -> typing.Self:
        """Adds an HTTP resource command"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['path'] = path
        rpc_args['displayName'] = display_name
        if options is not None:
            rpc_args['options'] = options
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpCommand',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_developer_certificate_trust(self, trust: bool) -> typing.Self:
        """Configures developer certificate trust"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['trust'] = trust
        result = self._client.invoke_capability(
            'Aspire.Hosting/withDeveloperCertificateTrust',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> typing.Self:
        """Sets the certificate trust scope"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['scope'] = scope
        result = self._client.invoke_capability(
            'Aspire.Hosting/withCertificateTrustScope',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_https_developer_certificate(self, *, password: ParameterResource | None = None) -> typing.Self:
        """Configures HTTPS with a developer certificate"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if password is not None:
            rpc_args['password'] = password
        result = self._client.invoke_capability(
            'Aspire.Hosting/withParameterHttpsDeveloperCertificate',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def without_https_certificate(self) -> typing.Self:
        """Removes HTTPS certificate configuration"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withoutHttpsCertificate',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_http_probe(self, probe_type: ProbeType, *, path: str | None = None, initial_delay_seconds: int | None = None, period_seconds: int | None = None, timeout_seconds: int | None = None, failure_threshold: int | None = None, success_threshold: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health probe to the resource"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['probeType'] = probe_type
        if path is not None:
            rpc_args['path'] = path
        if initial_delay_seconds is not None:
            rpc_args['initialDelaySeconds'] = initial_delay_seconds
        if period_seconds is not None:
            rpc_args['periodSeconds'] = period_seconds
        if timeout_seconds is not None:
            rpc_args['timeoutSeconds'] = timeout_seconds
        if failure_threshold is not None:
            rpc_args['failureThreshold'] = failure_threshold
        if success_threshold is not None:
            rpc_args['successThreshold'] = success_threshold
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withHttpProbe',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_image_push_options(self, callback: typing.Callable[[ContainerImagePushOptionsCallbackContext], None]) -> typing.Self:
        """Sets image push options via callback"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/withImagePushOptions',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_remote_image_name(self, remote_image_name: str) -> typing.Self:
        """Sets the remote image name for publishing"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['remoteImageName'] = remote_image_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withRemoteImageName',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_remote_image_tag(self, remote_image_tag: str) -> typing.Self:
        """Sets the remote image tag for publishing"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['remoteImageTag'] = remote_image_tag
        result = self._client.invoke_capability(
            'Aspire.Hosting/withRemoteImageTag',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def on_resource_endpoints_allocated(self, callback: typing.Callable[[ResourceEndpointsAllocatedEvent], None]) -> typing.Self:
        """Subscribes to the ResourceEndpointsAllocated event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/onResourceEndpointsAllocated',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

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

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[ExecutableResourceKwargs]) -> None:
        if _publish_as_docker_file := kwargs.pop("publish_as_docker_file", None):
            if _validate_type(_publish_as_docker_file, typing.Callable[[ContainerResource], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["configure"] = client.register_callback(typing.cast(typing.Callable[[ContainerResource], None], _publish_as_docker_file))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/publishAsDockerFile', rpc_args))
            else:
                raise TypeError("Invalid type for option 'publish_as_docker_file'. Expected: Callable[[ContainerResource], None]")
        if _executable_command := kwargs.pop("executable_command", None):
            if _validate_type(_executable_command, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["command"] = typing.cast(str, _executable_command)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withExecutableCommand', rpc_args))
            else:
                raise TypeError("Invalid type for option 'executable_command'. Expected: str")
        if _working_dir := kwargs.pop("working_dir", None):
            if _validate_type(_working_dir, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["workingDirectory"] = typing.cast(str, _working_dir)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withWorkingDirectory', rpc_args))
            else:
                raise TypeError("Invalid type for option 'working_dir'. Expected: str")
        if _mcp_server := kwargs.pop("mcp_server", None):
            if _validate_dict_types(_mcp_server, McpServerParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(McpServerParameters, _mcp_server).get("path")
                rpc_args["endpointName"] = typing.cast(McpServerParameters, _mcp_server).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withMcpServer', rpc_args))
            elif _mcp_server is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withMcpServer', rpc_args))
            else:
                raise TypeError("Invalid type for option 'mcp_server'. Expected: McpServerParameters or Literal[True]")
        if _otlp_exporter := kwargs.pop("otlp_exporter", None):
            if _validate_type(_otlp_exporter, OtlpProtocol):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["protocol"] = typing.cast(OtlpProtocol, _otlp_exporter)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withOtlpExporter', rpc_args))
            elif _otlp_exporter is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withOtlpExporter', rpc_args))
            else:
                raise TypeError("Invalid type for option 'otlp_exporter'. Expected: OtlpProtocol or Literal[True]")
        if _env := kwargs.pop("env", None):
            if _validate_tuple_types(_env, (str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue], _env)[0]
                rpc_args["value"] = typing.cast(tuple[str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue], _env)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEnvironment', rpc_args))
            else:
                raise TypeError("Invalid type for option 'env'. Expected: (str, str | ReferenceExpression | EndpointReference | ParameterResource | AbstractResourceWithConnectionString | AbstractExpressionValue)")
        if _env_callback := kwargs.pop("env_callback", None):
            if _validate_type(_env_callback, typing.Callable[[EnvironmentCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EnvironmentCallbackContext], None], _env_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEnvironmentCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'env_callback'. Expected: Callable[[EnvironmentCallbackContext], None]")
        if _args := kwargs.pop("args", None):
            if _validate_type(_args, typing.Iterable[str]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["args"] = typing.cast(typing.Iterable[str], _args)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withArgs', rpc_args))
            else:
                raise TypeError("Invalid type for option 'args'. Expected: Iterable[str]")
        if _args_callback := kwargs.pop("args_callback", None):
            if _validate_type(_args_callback, typing.Callable[[CommandLineArgsCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[CommandLineArgsCallbackContext], None], _args_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withArgsCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'args_callback'. Expected: Callable[[CommandLineArgsCallbackContext], None]")
        if _reference_env := kwargs.pop("reference_env", None):
            if _validate_type(_reference_env, ReferenceEnvironmentInjectionOptions):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["options"] = typing.cast(ReferenceEnvironmentInjectionOptions, _reference_env)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReferenceEnvironment', rpc_args))
            else:
                raise TypeError("Invalid type for option 'reference_env'. Expected: ReferenceEnvironmentInjectionOptions")
        if _reference := kwargs.pop("reference", None):
            if _validate_type(_reference, AbstractResource | EndpointReference | str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(AbstractResource | EndpointReference | str, _reference)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReference', rpc_args))
            elif _validate_dict_types(_reference, ReferenceParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(ReferenceParameters, _reference)["source"]
                rpc_args["connectionName"] = typing.cast(ReferenceParameters, _reference).get("connection_name")
                rpc_args["optional"] = typing.cast(ReferenceParameters, _reference).get("optional")
                rpc_args["name"] = typing.cast(ReferenceParameters, _reference).get("name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withReference', rpc_args))
            else:
                raise TypeError("Invalid type for option 'reference'. Expected: AbstractResource | EndpointReference | str or ReferenceParameters")
        if _endpoint_callback := kwargs.pop("endpoint_callback", None):
            if _validate_tuple_types(_endpoint_callback, (str, typing.Callable[[EndpointUpdateContext], None])):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(tuple[str, typing.Callable[[EndpointUpdateContext], None]], _endpoint_callback)[0]
                rpc_args["callback"] = client.register_callback(typing.cast(tuple[str, typing.Callable[[EndpointUpdateContext], None]], _endpoint_callback)[1])
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpointCallback', rpc_args))
            elif _validate_dict_types(_endpoint_callback, EndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["endpointName"] = typing.cast(EndpointCallbackParameters, _endpoint_callback)["endpoint_name"]
                rpc_args["callback"] = client.register_callback(typing.cast(EndpointCallbackParameters, _endpoint_callback)["callback"])
                rpc_args["createIfNotExists"] = typing.cast(EndpointCallbackParameters, _endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'endpoint_callback'. Expected: (str, Callable[[EndpointUpdateContext], None]) or EndpointCallbackParameters")
        if _http_endpoint_callback := kwargs.pop("http_endpoint_callback", None):
            if _validate_type(_http_endpoint_callback, typing.Callable[[EndpointUpdateContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EndpointUpdateContext], None], _http_endpoint_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpointCallback', rpc_args))
            elif _validate_dict_types(_http_endpoint_callback, HttpEndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback)["callback"])
                rpc_args["name"] = typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback).get("name")
                rpc_args["createIfNotExists"] = typing.cast(HttpEndpointCallbackParameters, _http_endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_endpoint_callback'. Expected: Callable[[EndpointUpdateContext], None] or HttpEndpointCallbackParameters")
        if _https_endpoint_callback := kwargs.pop("https_endpoint_callback", None):
            if _validate_type(_https_endpoint_callback, typing.Callable[[EndpointUpdateContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[EndpointUpdateContext], None], _https_endpoint_callback))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpointCallback', rpc_args))
            elif _validate_dict_types(_https_endpoint_callback, HttpsEndpointCallbackParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback)["callback"])
                rpc_args["name"] = typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback).get("name")
                rpc_args["createIfNotExists"] = typing.cast(HttpsEndpointCallbackParameters, _https_endpoint_callback).get("create_if_not_exists")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpointCallback', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_endpoint_callback'. Expected: Callable[[EndpointUpdateContext], None] or HttpsEndpointCallbackParameters")
        if _endpoint := kwargs.pop("endpoint", None):
            if _validate_dict_types(_endpoint, EndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(EndpointParameters, _endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(EndpointParameters, _endpoint).get("target_port")
                rpc_args["scheme"] = typing.cast(EndpointParameters, _endpoint).get("scheme")
                rpc_args["name"] = typing.cast(EndpointParameters, _endpoint).get("name")
                rpc_args["env"] = typing.cast(EndpointParameters, _endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(EndpointParameters, _endpoint).get("is_proxied")
                rpc_args["isExternal"] = typing.cast(EndpointParameters, _endpoint).get("is_external")
                rpc_args["protocol"] = typing.cast(EndpointParameters, _endpoint).get("protocol")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpoint', rpc_args))
            elif _endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'endpoint'. Expected: EndpointParameters or Literal[True]")
        if _http_endpoint := kwargs.pop("http_endpoint", None):
            if _validate_dict_types(_http_endpoint, HttpEndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("target_port")
                rpc_args["name"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("name")
                rpc_args["env"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(HttpEndpointParameters, _http_endpoint).get("is_proxied")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpoint', rpc_args))
            elif _http_endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_endpoint'. Expected: HttpEndpointParameters or Literal[True]")
        if _https_endpoint := kwargs.pop("https_endpoint", None):
            if _validate_dict_types(_https_endpoint, HttpsEndpointParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["port"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("port")
                rpc_args["targetPort"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("target_port")
                rpc_args["name"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("name")
                rpc_args["env"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("env")
                rpc_args["isProxied"] = typing.cast(HttpsEndpointParameters, _https_endpoint).get("is_proxied")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpoint', rpc_args))
            elif _https_endpoint is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpsEndpoint', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_endpoint'. Expected: HttpsEndpointParameters or Literal[True]")
        if _external_http_endpoints := kwargs.pop("external_http_endpoints", None):
            if _external_http_endpoints is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withExternalHttpEndpoints', rpc_args))
            else:
                raise TypeError("Invalid type for option 'external_http_endpoints'. Expected: Literal[True]")
        if _as_http2_service := kwargs.pop("as_http2_service", None):
            if _as_http2_service is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/asHttp2Service', rpc_args))
            else:
                raise TypeError("Invalid type for option 'as_http2_service'. Expected: Literal[True]")
        if _wait_for := kwargs.pop("wait_for", None):
            if _validate_type(_wait_for, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitFor', rpc_args))
            elif _validate_tuple_types(_wait_for, (AbstractResource, WaitBehavior)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for)[0]
                rpc_args["waitBehavior"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitFor', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for'. Expected: AbstractResource or (AbstractResource, WaitBehavior)")
        if _wait_for_start := kwargs.pop("wait_for_start", None):
            if _validate_type(_wait_for_start, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for_start)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForStart', rpc_args))
            elif _validate_tuple_types(_wait_for_start, (AbstractResource, WaitBehavior)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for_start)[0]
                rpc_args["waitBehavior"] = typing.cast(tuple[AbstractResource, WaitBehavior], _wait_for_start)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForStart', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for_start'. Expected: AbstractResource or (AbstractResource, WaitBehavior)")
        if _wait_for_completion := kwargs.pop("wait_for_completion", None):
            if _validate_type(_wait_for_completion, AbstractResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(AbstractResource, _wait_for_completion)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForResourceCompletion', rpc_args))
            elif _validate_tuple_types(_wait_for_completion, (AbstractResource, int)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["dependency"] = typing.cast(tuple[AbstractResource, int], _wait_for_completion)[0]
                rpc_args["exitCode"] = typing.cast(tuple[AbstractResource, int], _wait_for_completion)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/waitForResourceCompletion', rpc_args))
            else:
                raise TypeError("Invalid type for option 'wait_for_completion'. Expected: AbstractResource or (AbstractResource, int)")
        if _http_health_check := kwargs.pop("http_health_check", None):
            if _validate_dict_types(_http_health_check, HttpHealthCheckParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("path")
                rpc_args["statusCode"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("status_code")
                rpc_args["endpointName"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpHealthCheck', rpc_args))
            elif _http_health_check is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpHealthCheck', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_health_check'. Expected: HttpHealthCheckParameters or Literal[True]")
        if _http_command := kwargs.pop("http_command", None):
            if _validate_tuple_types(_http_command, (str, str)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(tuple[str, str], _http_command)[0]
                rpc_args["displayName"] = typing.cast(tuple[str, str], _http_command)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpCommand', rpc_args))
            elif _validate_dict_types(_http_command, HttpCommandParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(HttpCommandParameters, _http_command)["path"]
                rpc_args["displayName"] = typing.cast(HttpCommandParameters, _http_command)["display_name"]
                rpc_args["options"] = typing.cast(HttpCommandParameters, _http_command).get("options")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpCommand', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_command'. Expected: (str, str) or HttpCommandParameters")
        if _developer_certificate_trust := kwargs.pop("developer_certificate_trust", None):
            if _validate_type(_developer_certificate_trust, bool):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["trust"] = typing.cast(bool, _developer_certificate_trust)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDeveloperCertificateTrust', rpc_args))
            else:
                raise TypeError("Invalid type for option 'developer_certificate_trust'. Expected: bool")
        if _certificate_trust_scope := kwargs.pop("certificate_trust_scope", None):
            if _validate_type(_certificate_trust_scope, CertificateTrustScope):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["scope"] = typing.cast(CertificateTrustScope, _certificate_trust_scope)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withCertificateTrustScope', rpc_args))
            else:
                raise TypeError("Invalid type for option 'certificate_trust_scope'. Expected: CertificateTrustScope")
        if _https_developer_certificate := kwargs.pop("https_developer_certificate", None):
            if _validate_type(_https_developer_certificate, ParameterResource):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["password"] = typing.cast(ParameterResource, _https_developer_certificate)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withParameterHttpsDeveloperCertificate', rpc_args))
            elif _https_developer_certificate is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withParameterHttpsDeveloperCertificate', rpc_args))
            else:
                raise TypeError("Invalid type for option 'https_developer_certificate'. Expected: ParameterResource or Literal[True]")
        if _without_https_certificate := kwargs.pop("without_https_certificate", None):
            if _without_https_certificate is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withoutHttpsCertificate', rpc_args))
            else:
                raise TypeError("Invalid type for option 'without_https_certificate'. Expected: Literal[True]")
        if _http_probe := kwargs.pop("http_probe", None):
            if _validate_type(_http_probe, ProbeType):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["probeType"] = typing.cast(ProbeType, _http_probe)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpProbe', rpc_args))
            elif _validate_dict_types(_http_probe, HttpProbeParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["probeType"] = typing.cast(HttpProbeParameters, _http_probe)["probe_type"]
                rpc_args["path"] = typing.cast(HttpProbeParameters, _http_probe).get("path")
                rpc_args["initialDelaySeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("initial_delay_seconds")
                rpc_args["periodSeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("period_seconds")
                rpc_args["timeoutSeconds"] = typing.cast(HttpProbeParameters, _http_probe).get("timeout_seconds")
                rpc_args["failureThreshold"] = typing.cast(HttpProbeParameters, _http_probe).get("failure_threshold")
                rpc_args["successThreshold"] = typing.cast(HttpProbeParameters, _http_probe).get("success_threshold")
                rpc_args["endpointName"] = typing.cast(HttpProbeParameters, _http_probe).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withHttpProbe', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_probe'. Expected: ProbeType or HttpProbeParameters")
        if _image_push_options := kwargs.pop("image_push_options", None):
            if _validate_type(_image_push_options, typing.Callable[[ContainerImagePushOptionsCallbackContext], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ContainerImagePushOptionsCallbackContext], None], _image_push_options))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withImagePushOptions', rpc_args))
            else:
                raise TypeError("Invalid type for option 'image_push_options'. Expected: Callable[[ContainerImagePushOptionsCallbackContext], None]")
        if _remote_image_name := kwargs.pop("remote_image_name", None):
            if _validate_type(_remote_image_name, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["remoteImageName"] = typing.cast(str, _remote_image_name)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withRemoteImageName', rpc_args))
            else:
                raise TypeError("Invalid type for option 'remote_image_name'. Expected: str")
        if _remote_image_tag := kwargs.pop("remote_image_tag", None):
            if _validate_type(_remote_image_tag, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["remoteImageTag"] = typing.cast(str, _remote_image_tag)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withRemoteImageTag', rpc_args))
            else:
                raise TypeError("Invalid type for option 'remote_image_tag'. Expected: str")
        if _on_resource_endpoints_allocated := kwargs.pop("on_resource_endpoints_allocated", None):
            if _validate_type(_on_resource_endpoints_allocated, typing.Callable[[ResourceEndpointsAllocatedEvent], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ResourceEndpointsAllocatedEvent], None], _on_resource_endpoints_allocated))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/onResourceEndpointsAllocated', rpc_args))
            else:
                raise TypeError("Invalid type for option 'on_resource_endpoints_allocated'. Expected: Callable[[ResourceEndpointsAllocatedEvent], None]")
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


class DotnetToolResourceKwargs(ExecutableResourceKwargs, total=False):
    """DotnetToolResource options."""

    tool_package: str
    tool_version: str
    tool_prerelease: typing.Literal[True]
    tool_source: str
    tool_ignore_existing_feeds: typing.Literal[True]
    tool_ignore_failed_sources: typing.Literal[True]

class DotnetToolResource(ExecutableResource):
    """DotnetToolResource resource."""

    def __repr__(self) -> str:
        return "DotnetToolResource(handle={self._handle.handle_id})"

    def with_tool_package(self, package_id: str) -> typing.Self:
        """Sets the tool package ID"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['packageId'] = package_id
        result = self._client.invoke_capability(
            'Aspire.Hosting/withToolPackage',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_tool_version(self, version: str) -> typing.Self:
        """Sets the tool version"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['version'] = version
        result = self._client.invoke_capability(
            'Aspire.Hosting/withToolVersion',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_tool_prerelease(self) -> typing.Self:
        """Allows prerelease tool versions"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withToolPrerelease',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_tool_source(self, source: str) -> typing.Self:
        """Adds a NuGet source for the tool"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['source'] = source
        result = self._client.invoke_capability(
            'Aspire.Hosting/withToolSource',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_tool_ignore_existing_feeds(self) -> typing.Self:
        """Ignores existing NuGet feeds"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withToolIgnoreExistingFeeds',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def with_tool_ignore_failed_sources(self) -> typing.Self:
        """Ignores failed NuGet sources"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        result = self._client.invoke_capability(
            'Aspire.Hosting/withToolIgnoreFailedSources',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[DotnetToolResourceKwargs]) -> None:
        if _tool_package := kwargs.pop("tool_package", None):
            if _validate_type(_tool_package, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["packageId"] = typing.cast(str, _tool_package)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withToolPackage', rpc_args))
            else:
                raise TypeError("Invalid type for option 'tool_package'. Expected: str")
        if _tool_version := kwargs.pop("tool_version", None):
            if _validate_type(_tool_version, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["version"] = typing.cast(str, _tool_version)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withToolVersion', rpc_args))
            else:
                raise TypeError("Invalid type for option 'tool_version'. Expected: str")
        if _tool_prerelease := kwargs.pop("tool_prerelease", None):
            if _tool_prerelease is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withToolPrerelease', rpc_args))
            else:
                raise TypeError("Invalid type for option 'tool_prerelease'. Expected: Literal[True]")
        if _tool_source := kwargs.pop("tool_source", None):
            if _validate_type(_tool_source, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["source"] = typing.cast(str, _tool_source)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withToolSource', rpc_args))
            else:
                raise TypeError("Invalid type for option 'tool_source'. Expected: str")
        if _tool_ignore_existing_feeds := kwargs.pop("tool_ignore_existing_feeds", None):
            if _tool_ignore_existing_feeds is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withToolIgnoreExistingFeeds', rpc_args))
            else:
                raise TypeError("Invalid type for option 'tool_ignore_existing_feeds'. Expected: Literal[True]")
        if _tool_ignore_failed_sources := kwargs.pop("tool_ignore_failed_sources", None):
            if _tool_ignore_failed_sources is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withToolIgnoreFailedSources', rpc_args))
            else:
                raise TypeError("Invalid type for option 'tool_ignore_failed_sources'. Expected: Literal[True]")
        super().__init__(handle, client, **kwargs)


class ExternalServiceResourceKwargs(_BaseResourceKwargs, total=False):
    """ExternalServiceResource options."""

    http_health_check: HttpHealthCheckParameters | typing.Literal[True]

class ExternalServiceResource(_BaseResource):
    """ExternalServiceResource resource."""

    def __repr__(self) -> str:
        return "ExternalServiceResource(handle={self._handle.handle_id})"

    def with_http_health_check(self, *, path: str | None = None, status_code: int | None = None, endpoint_name: str | None = None) -> typing.Self:
        """Adds an HTTP health check to the external service"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        if path is not None:
            rpc_args['path'] = path
        if status_code is not None:
            rpc_args['statusCode'] = status_code
        if endpoint_name is not None:
            rpc_args['endpointName'] = endpoint_name
        result = self._client.invoke_capability(
            'Aspire.Hosting/withExternalServiceHttpHealthCheck',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[ExternalServiceResourceKwargs]) -> None:
        if _http_health_check := kwargs.pop("http_health_check", None):
            if _validate_dict_types(_http_health_check, HttpHealthCheckParameters):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["path"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("path")
                rpc_args["statusCode"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("status_code")
                rpc_args["endpointName"] = typing.cast(HttpHealthCheckParameters, _http_health_check).get("endpoint_name")
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withExternalServiceHttpHealthCheck', rpc_args))
            elif _http_health_check is True:
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withExternalServiceHttpHealthCheck', rpc_args))
            else:
                raise TypeError("Invalid type for option 'http_health_check'. Expected: HttpHealthCheckParameters or Literal[True]")
        super().__init__(handle, client, **kwargs)


class ParameterResourceKwargs(_BaseResourceKwargs, total=False):
    """ParameterResource options."""

    description: str | tuple[str, bool]

class ParameterResource(_BaseResource, AbstractExpressionValue):
    """ParameterResource resource."""

    def __repr__(self) -> str:
        return "ParameterResource(handle={self._handle.handle_id})"

    def with_description(self, description: str, *, enable_markdown: bool = False) -> typing.Self:
        """Sets a parameter description"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['description'] = description
        if enable_markdown is not None:
            rpc_args['enableMarkdown'] = enable_markdown
        result = self._client.invoke_capability(
            'Aspire.Hosting/withDescription',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[ParameterResourceKwargs]) -> None:
        if _description := kwargs.pop("description", None):
            if _validate_type(_description, str):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["description"] = typing.cast(str, _description)
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDescription', rpc_args))
            elif _validate_tuple_types(_description, (str, bool)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["description"] = typing.cast(tuple[str, bool], _description)[0]
                rpc_args["enableMarkdown"] = typing.cast(tuple[str, bool], _description)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withDescription', rpc_args))
            else:
                raise TypeError("Invalid type for option 'description'. Expected: str or (str, bool)")
        super().__init__(handle, client, **kwargs)


class TestDatabaseResourceKwargs(ContainerResourceKwargs, total=False):
    """TestDatabaseResource options."""


class TestDatabaseResource(ContainerResource):
    """TestDatabaseResource resource."""

    def __repr__(self) -> str:
        return "TestDatabaseResource(handle={self._handle.handle_id})"

    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[TestDatabaseResourceKwargs]) -> None:
        super().__init__(handle, client, **kwargs)


class TestRedisResourceKwargs(ContainerResourceKwargs, total=False):
    """TestRedisResource options."""

    connection_property: tuple[str, str | ReferenceExpression]
    on_connection_string_available: typing.Callable[[ConnectionStringAvailableEvent], None]
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

    def with_connection_property(self, name: str, value: str | ReferenceExpression) -> typing.Self:
        """Adds a connection property with a string or reference expression value"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['name'] = name
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting/withConnectionProperty',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

    def get_connection_property(self, key: str) -> ReferenceExpression:
        """Gets a connection property by key"""
        rpc_args: dict[str, typing.Any] = {'resource': self._handle}
        rpc_args['key'] = key
        result = self._client.invoke_capability(
            'Aspire.Hosting/getConnectionProperty',
            rpc_args,
        )
        return typing.cast(ReferenceExpression, result)

    def on_connection_string_available(self, callback: typing.Callable[[ConnectionStringAvailableEvent], None]) -> typing.Self:
        """Subscribes to the ConnectionStringAvailable event"""
        rpc_args: dict[str, typing.Any] = {'builder': self._handle}
        rpc_args['callback'] = self._client.register_callback(callback)
        result = self._client.invoke_capability(
            'Aspire.Hosting/onConnectionStringAvailable',
            rpc_args,
        )
        self._handle = self._wrap_builder(result)
        return self

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
        if _connection_property := kwargs.pop("connection_property", None):
            if _validate_tuple_types(_connection_property, (str, str | ReferenceExpression)):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["name"] = typing.cast(tuple[str, str | ReferenceExpression], _connection_property)[0]
                rpc_args["value"] = typing.cast(tuple[str, str | ReferenceExpression], _connection_property)[1]
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/withConnectionProperty', rpc_args))
            else:
                raise TypeError("Invalid type for option 'connection_property'. Expected: (str, str | ReferenceExpression)")
        if _on_connection_string_available := kwargs.pop("on_connection_string_available", None):
            if _validate_type(_on_connection_string_available, typing.Callable[[ConnectionStringAvailableEvent], None]):
                rpc_args: dict[str, typing.Any] = {"builder": handle}
                rpc_args["callback"] = client.register_callback(typing.cast(typing.Callable[[ConnectionStringAvailableEvent], None], _on_connection_string_available))
                handle = self._wrap_builder(client.invoke_capability('Aspire.Hosting/onConnectionStringAvailable', rpc_args))
            else:
                raise TypeError("Invalid type for option 'on_connection_string_available'. Expected: Callable[[ConnectionStringAvailableEvent], None]")
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
    app_host_file_path: str | None = None,
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
        app_host_file_path (str): The path to the AppHost source file. By default, this will use the ASPIRE_APPHOST_FILEPATH
            environment variable if set.
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
    if app_host_file_path is not None:
        effective_options['AppHostFilePath'] = app_host_file_path
    elif not effective_options.get('AppHostFilePath'):
        app_host_file_path = os.environ.get('ASPIRE_APPHOST_FILEPATH')
        if app_host_file_path:
            effective_options['AppHostFilePath'] = app_host_file_path
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
_register_handle_wrapper("Aspire.Hosting/Dict<string,any>", AspireDict)
_register_handle_wrapper("Aspire.Hosting/List<string>", AspireList)
_register_handle_wrapper("Aspire.Hosting/Dict<string,string>", AspireDict)
_register_handle_wrapper("Aspire.Hosting/Dict<string,number>", AspireDict)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IAspireStore", AbstractAspireStore)
_register_handle_wrapper("Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfiguration", AbstractConfiguration)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEventing", AbstractDistributedApplicationEventing)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IDistributedApplicationPipeline", AbstractDistributedApplicationPipeline)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExecutionConfigurationBuilder", AbstractExecutionConfigurationBuilder)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExecutionConfigurationResult", AbstractExecutionConfigurationResult)
_register_handle_wrapper("Microsoft.Extensions.Hosting.Abstractions/Microsoft.Extensions.Hosting.IHostEnvironment", AbstractHostEnvironment)
_register_handle_wrapper("Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILogger", AbstractLogger)
_register_handle_wrapper("Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILoggerFactory", AbstractLoggerFactory)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingStep", AbstractReportingStep)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingTask", AbstractReportingTask)
_register_handle_wrapper("System.ComponentModel/System.IServiceProvider", AbstractServiceProvider)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.IUserSecretsManager", AbstractUserSecretsManager)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.AfterResourcesCreatedEvent", AfterResourcesCreatedEvent)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeResourceStartedEvent", BeforeResourceStartedEvent)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeStartEvent", BeforeStartEvent)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsCallbackContext", CommandLineArgsCallbackContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsEditor", CommandLineArgsEditor)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ConnectionStringAvailableEvent", ConnectionStringAvailableEvent)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImagePushOptions", ContainerImagePushOptions)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImagePushOptionsCallbackContext", ContainerImagePushOptionsCallbackContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplication", DistributedApplication)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationEventSubscription", DistributedApplicationEventSubscription)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContext", DistributedApplicationExecutionContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DistributedApplicationModel", DistributedApplicationModel)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.Docker.DockerfileBuilder", DockerfileBuilder)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DockerfileBuilderCallbackContext", DockerfileBuilderCallbackContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.Docker.DockerfileStage", DockerfileStage)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference", EndpointReference)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReferenceExpression", EndpointReferenceExpression)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointUpdateContext", EndpointUpdateContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext", EnvironmentCallbackContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentEditor", EnvironmentEditor)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Ats.EventingSubscriberRegistrationContext", EventingSubscriberRegistrationContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecuteCommandContext", ExecuteCommandContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.InitializeResourceEvent", InitializeResourceEvent)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.LogFacade", LogFacade)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineConfigurationContext", PipelineConfigurationContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineContext", PipelineContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineEditor", PipelineEditor)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStep", PipelineStep)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepContext", PipelineStepContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepFactoryContext", PipelineStepFactoryContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineSummary", PipelineSummary)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ProjectResourceOptions", ProjectResourceOptions)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpressionBuilder", ReferenceExpressionBuilder)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceEndpointsAllocatedEvent", ResourceEndpointsAllocatedEvent)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceLoggerService", ResourceLoggerService)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceNotificationService", ResourceNotificationService)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceReadyEvent", ResourceReadyEvent)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceStoppedEvent", ResourceStoppedEvent)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsCallbackContext", ResourceUrlsCallbackContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsEditor", ResourceUrlsEditor)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", TestCallbackContext)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext", TestCollectionContext)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext", TestEnvironmentContext)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestMutableCollectionContext", TestMutableCollectionContext)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext", TestResourceContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.UpdateCommandStateContext", UpdateCommandStateContext)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.Resource", _BaseResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerRegistryResource", ContainerRegistryResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource", ContainerResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ProjectResource", ProjectResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CSharpAppResource", CSharpAppResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource", ExecutableResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DotnetToolResource", DotnetToolResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ExternalServiceResource", ExternalServiceResource)
_register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ParameterResource", ParameterResource)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource", TestDatabaseResource)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource", TestRedisResource)
_register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource", TestVaultResource)
