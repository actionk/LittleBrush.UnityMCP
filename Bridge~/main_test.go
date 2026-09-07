package main

import (
	"bufio"
	"context"
	"encoding/json"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"
)

func TestNewID(t *testing.T) {
	first := newID()
	second := newID()
	if len(first) != 32 || first == second {
		t.Fatalf("unexpected random IDs %q and %q", first, second)
	}
}

func TestDefaultBridgeTimeoutOutlivesUnityToolTimeout(t *testing.T) {
	total := defaultBufferTimeout + defaultRequestTimeout
	if total <= 5*time.Minute {
		t.Fatalf("bridge total timeout %v must exceed the five-minute approval window", total)
	}
}

func TestDefaultBufferTimeoutCoversSlowDomainReload(t *testing.T) {
	if defaultBufferTimeout < time.Minute {
		t.Fatalf("default buffer timeout %v is too short for slow Unity domain reloads", defaultBufferTimeout)
	}
}

func TestMarkReplayPayloadAddsHiddenArgument(t *testing.T) {
	body := []byte(`{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"editor.ensure_compiled","arguments":{"force":true}}}`)

	marked := markReplayPayload(body)

	var parsed struct {
		Params struct {
			Arguments map[string]interface{} `json:"arguments"`
		} `json:"params"`
	}
	if err := json.Unmarshal(marked, &parsed); err != nil {
		t.Fatalf("marked payload is invalid JSON: %v", err)
	}
	if parsed.Params.Arguments["force"] != true {
		t.Fatalf("force argument was not preserved: %#v", parsed.Params.Arguments)
	}
	if parsed.Params.Arguments["__mcpReplay"] != true {
		t.Fatalf("__mcpReplay marker missing: %#v", parsed.Params.Arguments)
	}
}

func TestGatewayPayloadUsesNestedToolForReplay(t *testing.T) {
	body := []byte(`{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"unity.call","arguments":{"tool":"editor.ensure_compiled","arguments":{"force":true}}}}`)
	bridge := &Bridge{reloadSafeTools: map[string]struct{}{"editor.ensure_compiled": {}}}

	if extractToolName(body) != "editor.ensure_compiled" {
		t.Fatalf("gateway target = %q", extractToolName(body))
	}
	if bridge.shouldRejectReplay(body) {
		t.Fatal("gateway editor.ensure_compiled should replay across reconnects")
	}

	marked := markReplayPayload(body)
	var parsed struct {
		Params struct {
			Arguments struct {
				Arguments map[string]interface{} `json:"arguments"`
			} `json:"arguments"`
		} `json:"params"`
	}
	if err := json.Unmarshal(marked, &parsed); err != nil {
		t.Fatalf("marked gateway payload is invalid JSON: %v", err)
	}
	if parsed.Params.Arguments.Arguments["__mcpReplay"] != true {
		t.Fatalf("nested __mcpReplay marker missing: %#v", parsed.Params.Arguments.Arguments)
	}

}

func TestMarkBridgeSessionPayloadAddsInternalSessionMetadata(t *testing.T) {
	body := []byte(`{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"unity.call","arguments":{"tool":"scene.write"},"_meta":{"progressToken":"p"}}}`)

	marked := markBridgeSessionPayload(body, "session-a")

	var parsed struct {
		Params struct {
			Meta map[string]interface{} `json:"_meta"`
		} `json:"params"`
	}
	if err := json.Unmarshal(marked, &parsed); err != nil {
		t.Fatalf("marked payload is invalid JSON: %v", err)
	}
	if parsed.Params.Meta["bridgeSessionId"] != "session-a" {
		t.Fatalf("bridgeSessionId = %#v", parsed.Params.Meta["bridgeSessionId"])
	}
	if parsed.Params.Meta["progressToken"] != "p" {
		t.Fatalf("existing metadata was not preserved: %#v", parsed.Params.Meta)
	}
}

func TestReplayRejectsNonReloadSafeTool(t *testing.T) {
	body := []byte(`{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"scene.write","arguments":{"operations":[]}}}`)
	bridge := &Bridge{reloadSafeTools: map[string]struct{}{"editor.status": {}}}

	if !bridge.shouldRejectReplay(body) {
		t.Fatalf("scene.write must not replay across Unity reconnects")
	}
}

func TestReplayAllowsReloadSafeTool(t *testing.T) {
	body := []byte(`{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"prefab.read","arguments":{}}}`)
	bridge := &Bridge{reloadSafeTools: map[string]struct{}{"prefab.read": {}}}

	if bridge.shouldRejectReplay(body) {
		t.Fatalf("prefab.read should replay when Unity declares it reload-safe")
	}
}

func TestReplayPolicyDefaultsToProtocolMethodsOnly(t *testing.T) {
	bridge := &Bridge{}
	for _, method := range []string{"initialize", "ping", "tools/list"} {
		body := []byte(`{"jsonrpc":"2.0","id":1,"method":"` + method + `"}`)
		if bridge.shouldRejectReplay(body) {
			t.Fatalf("protocol method %s should remain reload-safe", method)
		}
	}
	body := []byte(`{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"editor.status","arguments":{}}}`)
	if !bridge.shouldRejectReplay(body) {
		t.Fatal("tool must be rejected until Unity supplies its reload-safe policy")
	}
}

func TestRememberBackendReplacesReloadSafePolicy(t *testing.T) {
	bridge := &Bridge{reloadSafeTools: map[string]struct{}{"old.tool": {}}}
	bridge.rememberBackend(UnityHello{ReloadSafeTools: []string{"prefab.read", "editor.metrics"}})

	for _, name := range []string{"prefab.read", "editor.metrics"} {
		body := []byte(`{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"` + name + `","arguments":{}}}`)
		if bridge.shouldRejectReplay(body) {
			t.Fatalf("Unity-declared tool %s should replay", name)
		}
	}
	old := []byte(`{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"old.tool","arguments":{}}}`)
	if !bridge.shouldRejectReplay(old) {
		t.Fatal("stale reload-safe tool remained after a new handshake")
	}
}

func TestOriginGuardRejectsNonLocalOrigin(t *testing.T) {
	called := false
	handler := originGuard(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		called = true
		w.WriteHeader(http.StatusNoContent)
	}))
	req := httptest.NewRequest(http.MethodPost, "/mcp", strings.NewReader(`{}`))
	req.Header.Set("Origin", "https://evil.example")
	rec := httptest.NewRecorder()

	handler.ServeHTTP(rec, req)

	if called {
		t.Fatalf("handler was called for forbidden origin")
	}
	if rec.Code != http.StatusForbidden {
		t.Fatalf("status = %d, want %d", rec.Code, http.StatusForbidden)
	}
}

func TestHandleMcpAcceptsInitializedNotificationWithoutResponseBody(t *testing.T) {
	bridge := &Bridge{
		state:          StateWaitingUnity,
		pending:        make(map[string]*PendingRequest),
		buffered:       make([]*BufferedRequest, 0),
		sessions:       make(map[string]chan []byte),
		sseConns:       make(map[string]chan []byte),
		bufferTimeout:  time.Millisecond,
		requestTimeout: time.Millisecond,
		bufferMax:      1,
	}
	body := `{"jsonrpc":"2.0","method":"notifications/initialized"}`
	req := httptest.NewRequest(http.MethodPost, "/mcp", strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json, text/event-stream")
	sessionID, ok := bridge.newSession()
	if !ok {
		t.Fatal("failed to create test session")
	}
	req.Header.Set("Mcp-Session-Id", sessionID)
	rec := httptest.NewRecorder()

	bridge.handleMcp(rec, req)

	if rec.Code != http.StatusAccepted {
		t.Fatalf("status = %d, want %d; body: %q", rec.Code, http.StatusAccepted, rec.Body.String())
	}
	if rec.Body.Len() != 0 {
		t.Fatalf("body = %q, want empty body", rec.Body.String())
	}
}

func TestInitializeResponseCreatesSessionOnlyOnSuccess(t *testing.T) {
	bridge := &Bridge{
		sessions: make(map[string]chan []byte),
		sseConns: make(map[string]chan []byte),
	}
	rec := httptest.NewRecorder()
	payload := []byte(`{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18"}}`)

	bridge.writeMcpResponse(rec, payload, true)

	sessionID := rec.Header().Get("Mcp-Session-Id")
	if sessionID == "" {
		t.Fatal("successful initialize response did not create a session")
	}
	if _, ok := bridge.sessionChannel(sessionID); !ok {
		t.Fatalf("response session %q is not registered", sessionID)
	}

	failed := httptest.NewRecorder()
	bridge.writeMcpResponse(failed, []byte(`{"jsonrpc":"2.0","id":2,"error":{"code":-32603}}`), true)
	if failed.Header().Get("Mcp-Session-Id") != "" {
		t.Fatal("failed initialize response created a session")
	}
}

func TestSessionsAreBounded(t *testing.T) {
	bridge := &Bridge{
		sessions:        make(map[string]chan []byte),
		sessionLastSeen: make(map[string]time.Time),
		sseConns:        make(map[string]chan []byte),
	}
	for i := 0; i < maxSessions; i++ {
		if _, ok := bridge.newSession(); !ok {
			t.Fatalf("session %d was rejected before capacity", i)
		}
	}
	if _, ok := bridge.newSession(); ok {
		t.Fatal("session above capacity was accepted")
	}
	if len(bridge.sessions) != maxSessions {
		t.Fatalf("session count = %d, want %d", len(bridge.sessions), maxSessions)
	}
}

func TestIdleSessionsExpireButActiveSseDoesNot(t *testing.T) {
	bridge := &Bridge{
		sessions:        make(map[string]chan []byte),
		sessionLastSeen: make(map[string]time.Time),
		sseConns:        make(map[string]chan []byte),
	}
	idleID, _ := bridge.newSession()
	activeID, _ := bridge.newSession()
	bridge.sseMu.Lock()
	old := time.Now().Add(-sessionIdleTimeout - time.Minute)
	bridge.sessionLastSeen[idleID] = old
	bridge.sessionLastSeen[activeID] = old
	bridge.sseConns[activeID] = bridge.sessions[activeID]
	removed := bridge.evictExpiredSessionsLocked(time.Now())
	bridge.sseMu.Unlock()

	if removed != 1 {
		t.Fatalf("removed = %d, want 1", removed)
	}
	if _, ok := bridge.sessions[idleID]; ok {
		t.Fatal("idle session did not expire")
	}
	if _, ok := bridge.sessions[activeID]; !ok {
		t.Fatal("active SSE session expired")
	}
}

func TestCancellationNotificationCancelsMatchingRequestIDOnly(t *testing.T) {
	bridge := &Bridge{
		pending: map[string]*PendingRequest{
			"numeric": {ID: "numeric", Body: []byte(`{"jsonrpc":"2.0","id":7,"method":"tools/call"}`)},
			"string":  {ID: "string", Body: []byte(`{"jsonrpc":"2.0","id":"7","method":"tools/call"}`)},
		},
	}

	bridge.handleCancellationNotification([]byte(`{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}`))

	if _, ok := bridge.pending["numeric"]; ok {
		t.Fatal("numeric request was not cancelled")
	}
	if _, ok := bridge.pending["string"]; !ok {
		t.Fatal("string request ID must not collide with numeric request ID")
	}
}

func TestHandleMcpRejectsNonObjectJSON(t *testing.T) {
	for _, body := range []string{`null`, `[]`, `"scalar"`} {
		t.Run(body, func(t *testing.T) {
			bridge := &Bridge{}
			req := httptest.NewRequest(http.MethodPost, "/mcp", strings.NewReader(body))
			req.Header.Set("Content-Type", "application/json")
			req.Header.Set("Accept", "application/json, text/event-stream")
			rec := httptest.NewRecorder()

			bridge.handleMcp(rec, req)

			if rec.Code != http.StatusBadRequest {
				t.Fatalf("status = %d, want %d; body: %q", rec.Code, http.StatusBadRequest, rec.Body.String())
			}
			if !strings.Contains(rec.Body.String(), `"code":-32600`) {
				t.Fatalf("body = %q, want invalid-request error", rec.Body.String())
			}
		})
	}
}

func TestInitializedSessionReceivesToolListChanged(t *testing.T) {
	bridge := &Bridge{
		sessions: make(map[string]chan []byte),
		sseConns: make(map[string]chan []byte),
	}
	sessionID, ok := bridge.newSession()
	if !ok {
		t.Fatal("failed to create test session")
	}
	server := httptest.NewServer(http.HandlerFunc(bridge.handleMcp))
	defer server.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, server.URL+"/mcp", nil)
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("Accept", "application/json, text/event-stream")
	req.Header.Set("Mcp-Session-Id", sessionID)
	response, err := server.Client().Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()

	if response.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want %d", response.StatusCode, http.StatusOK)
	}
	if response.Header.Get("Location") != "" {
		t.Fatalf("unexpected redirect to %q", response.Header.Get("Location"))
	}

	notification := json.RawMessage(`{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}`)
	bridge.broadcastSse(notification)
	line, err := bufio.NewReader(response.Body).ReadString('\n')
	if err != nil {
		t.Fatal(err)
	}
	if line != "data: "+string(notification)+"\n" {
		t.Fatalf("event = %q, want tool-list notification", line)
	}
}

func TestHandleMcpRejectsUnsupportedProtocolVersion(t *testing.T) {
	bridge := &Bridge{}
	req := httptest.NewRequest(http.MethodPost, "/mcp", strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"ping"}`))
	req.Header.Set("MCP-Protocol-Version", "1999-01-01")
	rec := httptest.NewRecorder()

	bridge.handleMcp(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want %d", rec.Code, http.StatusBadRequest)
	}
}

func TestShouldAcceptBackendWhenNoActiveProcess(t *testing.T) {
	bridge := &Bridge{}
	hello := UnityHello{ProjectPath: "D:/Project/Assets", ProjectHash: "abc123", ProcessID: 100}

	if !bridge.shouldAcceptBackend(hello) {
		t.Fatalf("expected first backend to be accepted")
	}
}

func TestShouldAcceptBackendWhenSameProcessReconnects(t *testing.T) {
	bridge := &Bridge{activeProcessID: 100, activeProjectHash: "abc123"}
	hello := UnityHello{ProjectPath: "D:/Project/Assets", ProjectHash: "abc123", ProcessID: 100}

	if !bridge.shouldAcceptBackend(hello) {
		t.Fatalf("expected same Unity process reconnect to be accepted")
	}
}

func TestShouldAcceptBackendWhenDisconnectedSameProjectNewProcessConnects(t *testing.T) {
	bridge := &Bridge{activeProjectPath: "D:/Project/Assets", activeProjectHash: "abc123", activeProcessID: 100}
	hello := UnityHello{ProjectPath: "D:/Project/Assets", ProjectHash: "abc123", ProcessID: 200}

	if !bridge.shouldAcceptBackend(hello) {
		t.Fatalf("expected disconnected same project backend to be accepted")
	}
}

func TestShouldRejectBackendWhenDifferentProcessConnects(t *testing.T) {
	activeConn, peer := net.Pipe()
	defer activeConn.Close()
	defer peer.Close()
	bridge := &Bridge{activeProcessID: 100, activeProjectHash: "abc123", unityConn: activeConn}
	hello := UnityHello{ProjectPath: "D:/Project/Assets", ProjectHash: "abc123", ProcessID: 200}

	if bridge.shouldAcceptBackend(hello) {
		t.Fatalf("expected different Unity process to be rejected")
	}
}

func TestRejectedHandshakeDoesNotPublishOrMovePending(t *testing.T) {
	activeConn, activePeer := net.Pipe()
	defer activeConn.Close()
	defer activePeer.Close()
	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()
	bridge := &Bridge{
		state:             StateReady,
		pending:           map[string]*PendingRequest{"req": {ID: "req", ConnID: 1}},
		buffered:          make([]*BufferedRequest, 0),
		sseConns:          make(map[string]chan []byte),
		activeProjectHash: "abc123",
		activeProcessID:   100,
		unityConn:         activeConn,
		unityConnID:       1,
	}
	done := make(chan struct{})
	go func() {
		bridge.handleUnityHandshake(server, bufio.NewReader(server), bufio.NewWriter(server))
		close(done)
	}()

	if _, err := client.Write([]byte(`{"type":"hello","projectPath":"D:/Project/Assets","projectHash":"abc123","processId":200}` + "\n")); err != nil {
		t.Fatalf("write hello: %v", err)
	}
	if err := client.SetReadDeadline(time.Now().Add(time.Second)); err != nil {
		t.Fatalf("set read deadline: %v", err)
	}
	line, err := bufio.NewReader(client).ReadString('\n')
	if err != nil {
		t.Fatalf("read handshake response: %v", err)
	}
	var response map[string]string
	if err := json.Unmarshal([]byte(line), &response); err != nil {
		t.Fatalf("invalid handshake response: %v", err)
	}
	if response["type"] != "error" {
		t.Fatalf("response type = %q, want error", response["type"])
	}
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatalf("handshake did not finish after rejection")
	}
	if bridge.unityConn != activeConn {
		t.Fatalf("active connection was replaced")
	}
	if bridge.unityConnID != 1 {
		t.Fatalf("conn id changed to %d", bridge.unityConnID)
	}
	if len(bridge.pending) != 1 {
		t.Fatalf("pending count changed to %d", len(bridge.pending))
	}
	if len(bridge.buffered) != 0 {
		t.Fatalf("buffered count changed to %d", len(bridge.buffered))
	}
}

func TestRejectingDifferentBackendDoesNotMovePending(t *testing.T) {
	activeConn, peer := net.Pipe()
	defer activeConn.Close()
	defer peer.Close()
	bridge := &Bridge{
		state:             StateReady,
		pending:           map[string]*PendingRequest{"req": {ID: "req", ConnID: 1}},
		buffered:          make([]*BufferedRequest, 0),
		sseConns:          make(map[string]chan []byte),
		activeProjectHash: "abc123",
		activeProcessID:   100,
		unityConn:         activeConn,
		unityConnID:       1,
	}
	hello := UnityHello{ProjectPath: "D:/Project/Assets", ProjectHash: "abc123", ProcessID: 200}

	if bridge.shouldAcceptBackend(hello) {
		t.Fatalf("expected different process backend to be rejected")
	}
	if len(bridge.pending) != 1 {
		t.Fatalf("pending count changed to %d", len(bridge.pending))
	}
	if len(bridge.buffered) != 0 {
		t.Fatalf("buffered count changed to %d", len(bridge.buffered))
	}
}

func TestHealthIncludesBackendMetadata(t *testing.T) {
	bridge := &Bridge{
		state:             StateReady,
		pending:           map[string]*PendingRequest{"req": {ID: "req"}},
		buffered:          []*BufferedRequest{{ID: "buf"}},
		sseConns:          make(map[string]chan []byte),
		activeProjectPath: "D:/Project/Assets",
		activeProjectHash: "abc123",
		activeProcessID:   100,
		unityConnID:       7,
	}
	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodGet, "/mcp/health", nil)

	bridge.handleHealth(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want %d", rec.Code, http.StatusOK)
	}
	var payload map[string]interface{}
	if err := json.Unmarshal(rec.Body.Bytes(), &payload); err != nil {
		t.Fatalf("invalid health json: %v", err)
	}
	if payload["projectHash"] != "abc123" {
		t.Fatalf("projectHash = %#v, want abc123", payload["projectHash"])
	}
	if payload["bridgeProcessId"].(float64) != float64(os.Getpid()) {
		t.Fatalf("bridgeProcessId = %#v, want %d", payload["bridgeProcessId"], os.Getpid())
	}
	if payload["processId"].(float64) != 100 {
		t.Fatalf("processId = %#v, want 100", payload["processId"])
	}
	if payload["pendingRequests"].(float64) != 1 {
		t.Fatalf("pendingRequests = %#v, want 1", payload["pendingRequests"])
	}
	if payload["bufferedRequests"].(float64) != 1 {
		t.Fatalf("bufferedRequests = %#v, want 1", payload["bufferedRequests"])
	}
}
