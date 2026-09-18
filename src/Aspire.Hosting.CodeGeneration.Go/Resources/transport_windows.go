package aspire

import (
	"io"
	"os"
	"syscall"
	"time"
)

func openConnection(socketPath string, timeout time.Duration) (io.ReadWriteCloser, error) {
	pipePath := `\\.\pipe\` + socketPath
	path, err := syscall.UTF16PtrFromString(pipePath)
	if err != nil {
		return nil, err
	}

	// A synchronous pipe handle serializes reads and writes, so the background
	// reader can block the authentication request from being written.
	// Go 1.25+ os.NewFile integrates overlapped handles with the runtime poller.
	// https://learn.microsoft.com/windows/win32/ipc/synchronous-and-overlapped-input-and-output
	handle, err := syscall.CreateFile(path, syscall.GENERIC_READ|syscall.GENERIC_WRITE,
		0, nil, syscall.OPEN_EXISTING, syscall.FILE_FLAG_OVERLAPPED, 0)
	if err != nil {
		return nil, err
	}
	return os.NewFile(uintptr(handle), pipePath), nil
}
