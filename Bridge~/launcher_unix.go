//go:build darwin || linux

package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
)

func checkProjectClosed(project string) error {
	file, err := os.OpenFile(filepath.Join(project, "Temp", "UnityLockfile"), os.O_RDWR, 0)
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return fmt.Errorf("cannot check Unity project lock: %w", err)
	}
	defer file.Close()
	lock := syscall.Flock_t{Type: syscall.F_WRLCK, Whence: 0}
	if err := syscall.FcntlFlock(file.Fd(), syscall.F_SETLK, &lock); err != nil {
		return fmt.Errorf("project is already open; reconnect the existing Editor's MCP: %w", err)
	}
	return nil
}

func tryLaunchLock(file *os.File) bool {
	return syscall.Flock(int(file.Fd()), syscall.LOCK_EX|syscall.LOCK_NB) == nil
}
func hideWorker(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true} }

func workerProcessAlive(pid int) bool { return pid > 0 && syscall.Kill(pid, 0) != syscall.ESRCH }
