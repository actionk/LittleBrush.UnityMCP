//go:build windows

package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"unsafe"
)

var lockFileEx = syscall.NewLazyDLL("kernel32.dll").NewProc("LockFileEx")

// Unity holds this file exclusively. Its presence alone is not proof of a live Editor.
func checkProjectClosed(project string) error {
	path, err := syscall.UTF16PtrFromString(filepath.Join(project, "Temp", "UnityLockfile"))
	if err != nil {
		return err
	}
	handle, err := syscall.CreateFile(path, syscall.GENERIC_READ, 0, nil, syscall.OPEN_EXISTING, syscall.FILE_ATTRIBUTE_NORMAL, 0)
	if err == syscall.ERROR_FILE_NOT_FOUND || err == syscall.ERROR_PATH_NOT_FOUND {
		return nil
	}
	if err != nil {
		return fmt.Errorf("project is already open or its Unity lock cannot be checked; reconnect the existing Editor's MCP instead of starting another Editor: %w", err)
	}
	return syscall.CloseHandle(handle)
}

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
