//go:build windows

package main

import (
	"os"
	"os/exec"
	"syscall"
	"unsafe"
)

var lockFileEx = syscall.NewLazyDLL("kernel32.dll").NewProc("LockFileEx")

// The OS releases the lock even when the client crashes. Hold the file until startup ends.
func tryLaunchLock(file *os.File) bool {
	var overlapped syscall.Overlapped
	ok, _, _ := lockFileEx.Call(file.Fd(), 3, 0, 1, 0, uintptr(unsafe.Pointer(&overlapped)))
	return ok != 0
}

func hideWorker(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x00000200} // new process group
}

func workerProcessAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	handle, err := syscall.OpenProcess(0x1000, false, uint32(pid)) // query limited information
	if err != nil {
		return err != syscall.Errno(87)
	} // Access denied means unknown, not dead.
	defer syscall.CloseHandle(handle)
	var code uint32
	return syscall.GetExitCodeProcess(handle, &code) != nil || code == 259
}
