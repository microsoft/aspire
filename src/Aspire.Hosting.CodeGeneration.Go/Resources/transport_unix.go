//go:build !windows

package aspire

import (
	"io"
	"net"
	"time"
)

func openConnection(socketPath string, timeout time.Duration) (io.ReadWriteCloser, error) {
	dialer := net.Dialer{}
	if timeout > 0 {
		dialer.Timeout = timeout
	}
	return dialer.Dial("unix", socketPath)
}
