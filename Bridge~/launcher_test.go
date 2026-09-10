package main

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestOfflineGatewaySchemasMatchRouter(t *testing.T) {
	data, err := os.ReadFile("../Editor/Transport/JsonRpcRouter.cs")
	if err != nil {
		t.Fatal(err)
	}
	var gateways struct {
		Tools []struct {
			Name        string
			InputSchema any
		}
	}
	if err := json.Unmarshal(offlineGateways, &gateways); err != nil {
		t.Fatal(err)
	}
	for i, name := range []string{"CatalogSchema", "CallSchema"} {
		match := regexp.MustCompile(`(?s)` + name + ` = JObject.Parse\(@"(.*?)"\);`).FindSubmatch(data)
		if len(match) != 2 {
			t.Fatalf("Cannot find %s", name)
		}
		var expected any
		if err := json.Unmarshal([]byte(strings.ReplaceAll(string(match[1]), `""`, `"`)), &expected); err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(expected, gateways.Tools[i].InputSchema) {
			t.Fatalf("Offline %s schema drifted from the router", name)
		}
	}
}

func TestStdioDiscoveryDoesNotStartUnity(t *testing.T) {
	project := t.TempDir()
	if err := os.Mkdir(filepath.Join(project, "ProjectSettings"), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(project, "ProjectSettings/ProjectVersion.txt"), []byte("m_EditorVersion: missing\n"), 0600); err != nil {
		t.Fatal(err)
	}
	input := strings.NewReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n")
	var output bytes.Buffer
	if err := runStdio(project, "missing.exe", 48765, 48766, time.Minute, time.Second, time.Second, input, &output); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(output.String(), `"unity.call"`) {
		t.Fatal(output.String())
	}
	if _, err := os.Stat(filepath.Join(project, "Logs")); !os.IsNotExist(err) {
		t.Fatal("Discovery touched worker startup")
	}
}

func TestLaunchLockReleasedWhenOwnerCloses(t *testing.T) {
	path := filepath.Join(t.TempDir(), "launch.lock")
	a, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		t.Fatal(err)
	}
	defer a.Close()
	b, err := os.OpenFile(path, os.O_RDWR, 0600)
	if err != nil {
		t.Fatal(err)
	}
	defer b.Close()
	if !tryLaunchLock(a) {
		t.Fatal("First lock failed")
	}
	if tryLaunchLock(b) {
		t.Fatal("Two clients own the startup lock")
	}
	a.Close()
	if !tryLaunchLock(b) {
		t.Fatal("Closing owner did not release lock")
	}
}

func TestConcurrentRequestsReuseOneSessionAndRejectAnotherProject(t *testing.T) {
	project := t.TempDir()
	var initializes atomic.Int32
	var foreign atomic.Bool
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/mcp/health" {
			path := filepath.Join(project, "Assets")
			if foreign.Load() {
				path = filepath.Join(project, "Other/Assets")
			}
			json.NewEncoder(w).Encode(workerHealth{State: "ready", ProjectPath: path, ProcessID: 42})
			return
		}
		body, _ := io.ReadAll(r.Body)
		if bytes.Contains(body, []byte(`"method":"initialize"`)) {
			initializes.Add(1)
			w.Header().Set("Mcp-Session-Id", "session")
			io.WriteString(w, `{"jsonrpc":"2.0","id":"launcher-init","result":{}}`)
			return
		}
		w.WriteHeader(http.StatusAccepted)
	}))
	defer server.Close()
	w := &workerLauncher{project: project, endpoint: server.URL + "/mcp", client: server.Client(), startup: time.Second, gate: make(chan struct{}, 1)}
	var wg sync.WaitGroup
	for range 4 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			session, err := w.ensure(context.Background())
			if err != nil || session != "session" {
				t.Errorf("ensure: %s, %v", session, err)
			}
		}()
	}
	wg.Wait()
	if initializes.Load() != 1 {
		t.Fatalf("Created %d sessions", initializes.Load())
	}
	foreign.Store(true)
	if _, err := w.ensure(context.Background()); err == nil || !strings.Contains(err.Error(), "another project") {
		t.Fatalf("Foreign project accepted: %v", err)
	}
}

func TestPostNeverReplaysFailedWrite(t *testing.T) {
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		http.Error(w, "lost result", http.StatusServiceUnavailable)
	}))
	defer server.Close()
	w := &workerLauncher{endpoint: server.URL, client: server.Client()}
	_, _, err := w.post(context.Background(), []byte(`{"method":"tools/call"}`), "session")
	if err == nil || calls.Load() != 1 {
		t.Fatalf("err=%v calls=%d", err, calls.Load())
	}
}

func TestCancelledRequestDoesNotWaitForAnotherStartup(t *testing.T) {
	w := &workerLauncher{gate: make(chan struct{}, 1)}
	w.gate <- struct{}{}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := w.ensure(ctx); err != context.Canceled {
		t.Fatalf("Expected cancellation, got %v", err)
	}
}

func TestWorkerProcessLiveness(t *testing.T) {
	if !workerProcessAlive(os.Getpid()) {
		t.Fatal("This process must be alive")
	}
	if workerProcessAlive(0) || workerProcessAlive(2147483647) {
		t.Fatal("Missing process reported alive")
	}
}
