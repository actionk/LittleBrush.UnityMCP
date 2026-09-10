//go:build darwin || linux

package main

import (
	"os"
	"os/exec"
	"syscall"
)

func tryLaunchLock(file *os.File) bool {
	return syscall.Flock(int(file.Fd()), syscall.LOCK_EX|syscall.LOCK_NB) == nil
}
func hideWorker(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true} }

func workerProcessAlive(pid int) bool { return pid > 0 && syscall.Kill(pid, 0) != syscall.ESRCH }
