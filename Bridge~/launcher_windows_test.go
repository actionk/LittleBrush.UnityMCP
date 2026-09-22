//go:build windows

package main

import (
	"context"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"testing"
	"time"
)

func TestUnavailableBridgeNeverStartsAnotherEditorForLockedProject(t *testing.T) {
	project := t.TempDir()
	if err := os.Mkdir(filepath.Join(project, "Temp"), 0700); err != nil {
		t.Fatal(err)
	}
	path, _ := syscall.UTF16PtrFromString(filepath.Join(project, "Temp", "UnityLockfile"))
	handle, err := syscall.CreateFile(path, syscall.GENERIC_READ|syscall.GENERIC_WRITE, 0, nil, syscall.CREATE_ALWAYS, syscall.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer syscall.CloseHandle(handle)
	server := httptest.NewServer(http.NotFoundHandler())
	endpoint := server.URL + "/mcp"
	server.Close()
	w := &workerLauncher{project: project, editor: "must-not-launch.exe", endpoint: endpoint,
		client: &http.Client{Timeout: time.Second}, startup: time.Second, gate: make(chan struct{}, 1)}
	if _, err := w.ensure(context.Background()); err == nil || !strings.Contains(err.Error(), "already open") {
		t.Fatalf("Locked project must fail before executable lookup: %v", err)
	}
	syscall.CloseHandle(handle)
	if err := checkProjectClosed(project); err != nil {
		t.Fatalf("Stale unlocked file must not block startup: %v", err)
	}
}
