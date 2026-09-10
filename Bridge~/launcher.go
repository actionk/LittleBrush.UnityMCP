package main

import (
	"bufio"
	"bytes"
	"context"
	_ "embed"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Only the two stable gateway schemas are available offline. Unity owns the tool catalog.
//
//go:embed gateway.json
var offlineGateways json.RawMessage

type workerLauncher struct {
	project, editor, endpoint string
	port, unityPort           int
	idle, startup             time.Duration
	client                    *http.Client
	mu                        sync.Mutex
	gate                      chan struct{}
	session                   string
	processID                 int
}

type workerHealth struct {
	State       string `json:"state"`
	ProjectPath string `json:"projectPath"`
	ProcessID   int    `json:"processId"`
}

func sameProject(actual, expected string) bool {
	actual = filepath.Clean(actual)
	if filepath.Base(actual) == "Assets" {
		actual = filepath.Dir(actual)
	}
	if runtime.GOOS == "windows" {
		return strings.EqualFold(actual, filepath.Clean(expected))
	}
	return actual == filepath.Clean(expected)
}

func (w *workerLauncher) health(ctx context.Context) (*workerHealth, error) {
	ctx, cancel := context.WithTimeout(ctx, 2*time.Second)
	defer cancel()
	req, _ := http.NewRequestWithContext(ctx, "GET", w.endpoint+"/health", nil)
	resp, err := w.client.Do(req)
	if err != nil {
		return nil, nil
	}
	defer resp.Body.Close()
	var health workerHealth
	if resp.StatusCode != 200 || json.NewDecoder(io.LimitReader(resp.Body, 64<<10)).Decode(&health) != nil {
		return nil, errors.New("configured MCP port is occupied by an unrecognized server")
	}
	if health.ProjectPath != "" && !sameProject(health.ProjectPath, w.project) {
		return nil, fmt.Errorf("configured MCP port belongs to another project: %s", health.ProjectPath)
	}
	return &health, nil
}

func pause(ctx context.Context) error {
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(250 * time.Millisecond):
		return nil
	}
}

func locateEditor(project, explicit string) (string, error) {
	if explicit != "" {
		path, err := filepath.Abs(explicit)
		if err != nil {
			return "", err
		}
		if _, err := os.Stat(path); err != nil {
			return "", err
		}
		return path, nil
	}
	data, err := os.ReadFile(filepath.Join(project, "ProjectSettings", "ProjectVersion.txt"))
	if err != nil {
		return "", err
	}
	var version string
	for _, line := range strings.Split(string(data), "\n") {
		if strings.HasPrefix(line, "m_EditorVersion:") {
			version = strings.TrimSpace(strings.TrimPrefix(line, "m_EditorVersion:"))
		}
	}
	if version == "" || strings.ContainsAny(version, `/\`) {
		return "", errors.New("invalid ProjectVersion.txt")
	}
	home, _ := os.UserHomeDir()
	paths := []string{
		filepath.Join(os.Getenv("ProgramFiles"), "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"),
		filepath.Join("/Applications/Unity/Hub/Editor", version, "Unity.app/Contents/MacOS/Unity"),
		filepath.Join(home, "Unity/Hub/Editor", version, "Editor/Unity"),
	}
	for _, path := range paths {
		if info, err := os.Stat(path); err == nil && !info.IsDir() {
			return path, nil
		}
	}
	return "", fmt.Errorf("Unity %s is not installed in a standard Hub location; supply --editor with that version's executable", version)
}

func (w *workerLauncher) ensure(ctx context.Context) (string, error) {
	select {
	case w.gate <- struct{}{}:
	case <-ctx.Done():
		return "", ctx.Err()
	}
	defer func() { <-w.gate }()
	w.mu.Lock()
	defer w.mu.Unlock()
	ctx, cancel := context.WithTimeout(ctx, w.startup)
	defer cancel()
	health, err := w.health(ctx)
	if err != nil {
		return "", err
	}
	if health == nil || health.State != "ready" || health.ProjectPath == "" {
		logs := filepath.Join(w.project, "Logs")
		if err := os.MkdirAll(logs, 0755); err != nil {
			return "", err
		}
		lock, err := os.OpenFile(filepath.Join(logs, "littlebrush-mcp-launch.lock"), os.O_CREATE|os.O_RDWR, 0600)
		if err != nil {
			return "", err
		}
		defer lock.Close()
		for !tryLaunchLock(lock) {
			if err := pause(ctx); err != nil {
				return "", err
			}
		}
		health, err = w.health(ctx)
		if err != nil {
			return "", err
		}
		// Preserve a live Editor across reloads. Unity CLI can terminate its Editor
		// without the quitting callback, leaving a reusable native bridge behind.
		var exited chan error
		if health == nil || (health.ProcessID > 0 && !workerProcessAlive(health.ProcessID)) {
			editor, err := locateEditor(w.project, w.editor)
			if err != nil {
				return "", err
			}
			logPath := filepath.Join(logs, "littlebrush-mcp-worker.log")
			cmd := exec.Command(editor, "-batchmode", "-projectPath", w.project, "-logFile", logPath,
				"-littlebrushMcpWorker", "-littlebrushMcpIdleSeconds", strconv.FormatFloat(w.idle.Seconds(), 'f', -1, 64))
			cmd.Env = append(os.Environ(), "LITTLEBRUSH_MCP_PORT="+strconv.Itoa(w.port), "LITTLEBRUSH_MCP_UNITY_PORT="+strconv.Itoa(w.unityPort))
			hideWorker(cmd)
			if err := cmd.Start(); err != nil {
				return "", err
			}
			exited = make(chan error, 1)
			go func() { exited <- cmd.Wait() }()
			fmt.Fprintln(os.Stderr, "Starting Unity worker; log:", logPath)
		}
		for health == nil || health.State != "ready" || health.ProjectPath == "" {
			select {
			case err := <-exited:
				return "", fmt.Errorf("Unity exited before MCP became ready (%v); inspect %s", err, filepath.Join(logs, "littlebrush-mcp-worker.log"))
			default:
			}
			if err := pause(ctx); err != nil {
				return "", fmt.Errorf("Unity did not become ready: %w; inspect %s", err, filepath.Join(logs, "littlebrush-mcp-worker.log"))
			}
			health, err = w.health(ctx)
			if err != nil {
				return "", err
			}
		}
	}
	if w.session != "" && w.processID == health.ProcessID {
		return w.session, nil
	}
	request := []byte(`{"jsonrpc":"2.0","id":"launcher-init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"LittleBrush launcher","version":"1"}}}`)
	_, session, err := w.post(ctx, request, "")
	if err != nil {
		return "", err
	}
	if session == "" {
		return "", errors.New("bridge did not issue an MCP session")
	}
	w.session, w.processID = session, health.ProcessID
	_, _, err = w.post(ctx, []byte(`{"jsonrpc":"2.0","method":"notifications/initialized"}`), session)
	return session, err
}

func (w *workerLauncher) post(ctx context.Context, body []byte, session string) (json.RawMessage, string, error) {
	req, err := http.NewRequestWithContext(ctx, "POST", w.endpoint, bytes.NewReader(body))
	if err != nil {
		return nil, "", err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json, text/event-stream")
	req.Header.Set("MCP-Protocol-Version", "2025-06-18")
	if session != "" {
		req.Header.Set("Mcp-Session-Id", session)
	}
	resp, err := w.client.Do(req)
	if err != nil {
		return nil, "", err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, 64<<20+1))
	if err != nil {
		return nil, "", err
	}
	if len(data) > 64<<20 {
		return nil, "", errors.New("MCP response exceeds 64 MB")
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, "", fmt.Errorf("MCP HTTP %d: %s (request not retried)", resp.StatusCode, string(data))
	}
	return data, resp.Header.Get("Mcp-Session-Id"), nil
}

func runStdio(project, editor string, port, unityPort int, idle, startup, requestTimeout time.Duration, input io.Reader, output io.Writer) error {
	if project == "" || port < 1 || port > 65535 || unityPort < 1 || unityPort > 65535 || port == unityPort || idle < time.Second || startup <= 0 {
		return errors.New("--stdio requires --project, distinct valid ports and positive timeouts")
	}
	project, err := filepath.Abs(project)
	if err != nil {
		return err
	}
	project, err = filepath.EvalSymlinks(project)
	if err != nil {
		return err
	}
	if _, err := os.Stat(filepath.Join(project, "ProjectSettings", "ProjectVersion.txt")); err != nil {
		return err
	}
	w := &workerLauncher{project: project, editor: editor, endpoint: fmt.Sprintf("http://127.0.0.1:%d/mcp", port), port: port, unityPort: unityPort, idle: idle, startup: startup, client: &http.Client{Timeout: requestTimeout}, gate: make(chan struct{}, 1)}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	var outputMu sync.Mutex
	write := func(v any) { outputMu.Lock(); defer outputMu.Unlock(); _ = json.NewEncoder(output).Encode(v) }
	fail := func(id json.RawMessage, code int, message string) {
		write(map[string]any{"jsonrpc": "2.0", "id": id, "error": map[string]any{"code": code, "message": message}})
	}
	var pending sync.Map
	var wg sync.WaitGroup
	defer func() {
		cancel()
		wg.Wait()
		if w.session != "" {
			closeCtx, closeCancel := context.WithTimeout(context.Background(), 2*time.Second)
			defer closeCancel()
			req, _ := http.NewRequestWithContext(closeCtx, "DELETE", w.endpoint, nil)
			req.Header.Set("Mcp-Session-Id", w.session)
			if resp, err := w.client.Do(req); err == nil {
				resp.Body.Close()
			}
		}
	}()
	capacity := make(chan struct{}, 8)
	scanner := bufio.NewScanner(input)
	scanner.Buffer(make([]byte, 4096), maxRequestBodySize)
	for scanner.Scan() {
		body := append([]byte(nil), scanner.Bytes()...)
		var request struct {
			JSONRPC string          `json:"jsonrpc"`
			ID      json.RawMessage `json:"id"`
			Method  string          `json:"method"`
			Params  json.RawMessage `json:"params"`
		}
		if json.Unmarshal(body, &request) != nil {
			fail(nil, -32700, "Invalid JSON")
			continue
		}
		if request.JSONRPC != "2.0" || request.Method == "" {
			fail(request.ID, -32600, "Invalid JSON-RPC request")
			continue
		}
		if request.Method == "notifications/cancelled" {
			var p struct {
				RequestID json.RawMessage `json:"requestId"`
			}
			if json.Unmarshal(request.Params, &p) == nil {
				if c, ok := pending.Load(string(p.RequestID)); ok {
					c.(context.CancelFunc)()
				}
			}
			continue
		}
		if len(request.ID) == 0 {
			continue
		}
		result := func(v any) { write(map[string]any{"jsonrpc": "2.0", "id": request.ID, "result": v}) }
		switch request.Method {
		case "initialize":
			result(map[string]any{"protocolVersion": "2025-06-18", "capabilities": map[string]any{"tools": map[string]any{}}, "serverInfo": map[string]any{"name": "LittleBrushGames.UnityMCP.Launcher", "version": "1"}, "instructions": "Use unity.tools for schemas, then unity.call. Unity starts on the first tool request and managed workers stop when idle."})
		case "ping":
			result(map[string]any{})
		case "tools/list":
			result(offlineGateways)
		case "tools/call":
			select {
			case capacity <- struct{}{}:
			default:
				fail(request.ID, -32000, "Launcher busy; at most eight requests may be in flight")
				continue
			}
			callCtx, callCancel := context.WithCancel(ctx)
			if _, loaded := pending.LoadOrStore(string(request.ID), callCancel); loaded {
				callCancel()
				<-capacity
				fail(request.ID, -32600, "Duplicate in-flight request ID")
				continue
			}
			wg.Add(1)
			go func(id json.RawMessage, body []byte) {
				defer wg.Done()
				defer func() { <-capacity }()
				defer pending.Delete(string(id))
				defer callCancel()
				session, err := w.ensure(callCtx)
				if err != nil {
					fail(id, -32000, err.Error())
					return
				}
				response, _, err := w.post(callCtx, body, session)
				if err != nil {
					// Reconnect on the next request, never replay a possibly completed write.
					w.mu.Lock()
					if w.session == session {
						w.session = ""
					}
					w.mu.Unlock()
					fail(id, -32000, err.Error())
					return
				}
				if !json.Valid(response) {
					fail(id, -32000, "Bridge returned invalid JSON")
					return
				}
				write(response)
			}(request.ID, body)
		default:
			fail(request.ID, -32601, "Method not supported")
		}
	}
	return scanner.Err()
}
