package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func queueBridge() *Bridge {
	return &Bridge{state: StateWaitingUnity, pending: make(map[string]*PendingRequest),
		bufferMax: 8, bufferTimeout: time.Second, requestTimeout: time.Second,
		sessions: make(map[string]chan []byte), sseConns: make(map[string]chan []byte)}
}

func queueBody(tool string) []byte {
	return []byte(`{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"unity.call","arguments":{"tool":"` + tool + `","arguments":{}}}}`)
}

func waitQueue(t *testing.T, b *Bridge, want int) {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		b.mu.Lock()
		n := len(b.buffered)
		b.mu.Unlock()
		if n == want {
			return
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatalf("queue did not reach %d entries", want)
}

func queueBackend(t *testing.T, b *Bridge, safe ...string) (net.Conn, *json.Decoder) {
	t.Helper()
	server, client := net.Pipe()
	b.handleUnityConnection(server)
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	t.Cleanup(func() { _ = client.Close() })
	if err := json.NewEncoder(client).Encode(UnityHello{Type: "hello", Version: "1.0", ProcessID: 123,
		ProjectHash: "queue-test", ReloadSafeTools: safe}); err != nil {
		t.Fatal(err)
	}
	decoder := json.NewDecoder(client)
	var welcome map[string]interface{}
	if err := decoder.Decode(&welcome); err != nil {
		t.Fatal(err)
	}
	if welcome["type"] != "welcome" {
		t.Fatalf("welcome = %v", welcome)
	}
	return client, decoder
}

func queueResponse(t *testing.T, ch <-chan json.RawMessage) json.RawMessage {
	t.Helper()
	select {
	case response := <-ch:
		return response
	case <-time.After(3 * time.Second):
		t.Fatal("no response")
		return nil
	}
}

func TestFreshMutationWaitsForUnityAndExecutesOnceWithoutReplayMarker(t *testing.T) {
	for _, state := range []BridgeState{StateWaitingUnity, StateBuffering} {
		t.Run(state.String(), func(t *testing.T) {
			b := queueBridge()
			b.state = state
			session, _ := b.newSession()
			req := httptest.NewRequest(http.MethodPost, "/mcp", bytes.NewReader(queueBody("editor.execute_code")))
			req.Header.Set("Mcp-Session-Id", session)
			recorder := httptest.NewRecorder()
			done := make(chan struct{})
			go func() { defer close(done); b.handleMcp(recorder, req) }()
			waitQueue(t, b, 1)
			conn, decoder := queueBackend(t, b)
			var msg UnityMessage
			if err := decoder.Decode(&msg); err != nil {
				t.Fatal(err)
			}
			if msg.Type != "request" || extractToolName(msg.Payload) != "editor.execute_code" || bytes.Contains(msg.Payload, []byte("__mcpReplay")) {
				t.Fatalf("first dispatch = %+v", msg)
			}
			if err := json.NewEncoder(conn).Encode(UnityMessage{Type: "response", ID: msg.ID,
				Payload: json.RawMessage(`{"jsonrpc":"2.0","id":7,"result":{"applied":true}}`)}); err != nil {
				t.Fatal(err)
			}
			select {
			case <-done:
			case <-time.After(3 * time.Second):
				t.Fatal("HTTP request did not complete")
			}
			if recorder.Code != 200 || !strings.Contains(recorder.Body.String(), `"applied":true`) {
				t.Fatal(recorder.Body.String())
			}
			b.mu.Lock()
			defer b.mu.Unlock()
			if len(b.pending) != 0 || len(b.buffered) != 0 {
				t.Fatal("completed request retained for replay")
			}
		})
	}
}

func TestWriterDisappearsBeforeFirstDispatchQueuesMutationWithCapacityLimit(t *testing.T) {
	b := queueBridge()
	b.state = StateReady
	b.bufferMax = 1
	first := make(chan json.RawMessage, 1)
	b.sendRequestToUnity("first", queueBody("scene.write"), first)
	if len(b.buffered) != 1 || b.buffered[0].Replay {
		t.Fatal("unsent mutation was not queued as fresh")
	}
	second := make(chan json.RawMessage, 1)
	b.sendRequestToUnity("second", queueBody("scene.write"), second)
	if !bytes.Contains(queueResponse(t, second), []byte("BUFFER_FULL")) || len(b.buffered) != 1 {
		t.Fatal("queue limit not enforced")
	}
	conn, decoder := queueBackend(t, b)
	var msg UnityMessage
	if err := decoder.Decode(&msg); err != nil {
		t.Fatal(err)
	}
	if msg.ID != "first" || bytes.Contains(msg.Payload, []byte("__mcpReplay")) {
		t.Fatalf("unexpected dispatch: %+v", msg)
	}
	_ = conn.Close()
	if !bytes.Contains(queueResponse(t, first), []byte(`"executionState":"unknown"`)) {
		t.Fatal("lost mutation result was not reported as unknown")
	}
	waitQueue(t, b, 0)
}

func TestOnlyPreviouslyDispatchedReloadSafeRequestIsMarkedForReplay(t *testing.T) {
	b := queueBridge()
	response := make(chan json.RawMessage, 1)
	b.sendRequestToUnity("safe", queueBody("editor.status"), response)
	conn, decoder := queueBackend(t, b, "editor.status")
	var first UnityMessage
	if err := decoder.Decode(&first); err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(first.Payload, []byte("__mcpReplay")) {
		t.Fatal("first dispatch marked as replay")
	}
	_ = conn.Close()
	waitQueue(t, b, 1)
	conn2, decoder2 := queueBackend(t, b, "editor.status")
	var second UnityMessage
	if err := decoder2.Decode(&second); err != nil {
		t.Fatal(err)
	}
	if second.ID != first.ID || !bytes.Contains(second.Payload, []byte(`"__mcpReplay":true`)) {
		t.Fatalf("replay = %+v", second)
	}
	_ = json.NewEncoder(conn2).Encode(UnityMessage{Type: "response", ID: second.ID, Payload: json.RawMessage(`{"jsonrpc":"2.0","id":7,"result":{}}`)})
	queueResponse(t, response)
}

func TestCancelledOrExpiredQueuedMutationNeverReachesWriter(t *testing.T) {
	for _, mode := range []string{"cancelled", "expired", "policy-changed"} {
		t.Run(mode, func(t *testing.T) {
			b := queueBridge()
			b.unityConnID = 1
			var wire bytes.Buffer
			b.unityWriter = bufio.NewWriter(&wire)
			r := &BufferedRequest{ID: "queued", Body: queueBody("scene.write"), RespChan: make(chan json.RawMessage, 1), CreatedAt: time.Now()}
			b.buffered = append(b.buffered, r)
			switch mode {
			case "cancelled":
				b.cancelRequest(r.ID)
			case "expired":
				r.CreatedAt = time.Now().Add(-2 * time.Second)
			case "policy-changed":
				r.Replay = true
			}
			b.dispatchRequest(r, true, 1)
			if wire.Len() != 0 || len(b.pending) != 0 || len(b.buffered) != 0 {
				t.Fatal("invalid queued mutation dispatched")
			}
			if mode == "expired" && !bytes.Contains(queueResponse(t, r.RespChan), []byte("BUFFER_TIMEOUT")) {
				t.Fatal("missing expiry result")
			}
			if mode == "policy-changed" && !bytes.Contains(queueResponse(t, r.RespChan), []byte("REPLAY_REJECTED")) {
				t.Fatal("missing replay rejection")
			}
		})
	}
}

func TestCancellationCanRemoveRequestWhileItWaitsForWriterLock(t *testing.T) {
	b := queueBridge()
	b.unityConnID = 1
	var wire bytes.Buffer
	b.unityWriter = bufio.NewWriter(&wire)
	r := &BufferedRequest{ID: "cancel", Body: queueBody("scene.write"), RespChan: make(chan json.RawMessage, 1), CreatedAt: time.Now()}
	b.buffered = append(b.buffered, r)
	b.writeMu.Lock()
	done := make(chan struct{})
	go func() { defer close(done); b.dispatchRequest(r, true, 1) }()
	b.cancelRequest(r.ID)
	b.writeMu.Unlock()
	<-done
	if wire.Len() != 0 {
		t.Fatal("cancelled mutation was sent after the writer unlocked")
	}
}

type failedQueueWriter struct{}

func (failedQueueWriter) Write([]byte) (int, error) {
	return 0, errors.New("connection lost during write")
}

func TestFailedWriteDoesNotReplayMutationWithUnknownOutcome(t *testing.T) {
	b := queueBridge()
	b.state = StateReady
	b.unityWriter = bufio.NewWriter(failedQueueWriter{})
	response := make(chan json.RawMessage, 1)
	b.sendRequestToUnity("write", queueBody("scene.write"), response)
	if !bytes.Contains(queueResponse(t, response), []byte(`"executionState":"unknown"`)) {
		t.Fatal("missing unknown-result response")
	}
	if len(b.buffered) != 0 || len(b.pending) != 0 {
		t.Fatal("ambiguous mutation retained for automatic retry")
	}
}

func TestReplacingConnectionCollectsUnknownPendingEvenWhenOldReaderExited(t *testing.T) {
	b := queueBridge()
	old, _ := queueBackend(t, b)
	// Keep the old connection alive: replacement makes its reader stale before
	// it exits, so only the new handshake can recover the old pending request.
	response := make(chan json.RawMessage, 1)
	b.mu.Lock()
	b.pending["old"] = &PendingRequest{ID: "old", Body: queueBody("scene.write"), RespChan: response, ConnID: b.unityConnID}
	b.mu.Unlock()
	queueBackend(t, b)
	if !bytes.Contains(queueResponse(t, response), []byte("REPLAY_REJECTED")) {
		t.Fatal("old pending mutation was stranded")
	}
	_ = old.Close()
}

func TestReloadEventPausesFirstDispatchBeforeSocketDisconnects(t *testing.T) {
	b := queueBridge()
	b.state, b.unityConnID = StateReady, 1
	var wire bytes.Buffer
	b.unityWriter = bufio.NewWriter(&wire)
	b.handleLifecycleEvent("reloading", 1)
	response := make(chan json.RawMessage, 1)
	b.sendRequestToUnity("deferred", queueBody("scene.write"), response)
	if len(b.buffered) != 1 {
		t.Fatal("new mutation did not wait for reload")
	}
	if b.dispatchRequest(b.buffered[0], true, 1) || wire.Len() != 0 {
		t.Fatal("drain wrote into a reloading Unity connection")
	}
	conn, decoder := queueBackend(t, b)
	var msg UnityMessage
	if err := decoder.Decode(&msg); err != nil {
		t.Fatal(err)
	}
	if msg.ID != "deferred" || bytes.Contains(msg.Payload, []byte("__mcpReplay")) {
		t.Fatalf("first dispatch = %+v", msg)
	}
	_ = conn.Close()
	queueResponse(t, response)
}

func TestStaleReloadEventCannotPauseReplacementConnection(t *testing.T) {
	b := queueBridge()
	b.state, b.unityConnID = StateReady, 2
	b.handleLifecycleEvent("reloading", 1)
	if b.state != StateReady || b.unityReloading {
		t.Fatal("stale reader paused the replacement connection")
	}
}

func TestFreshQueueDrainsInOrderWhileReceivingResponses(t *testing.T) {
	b := queueBridge()
	responses := make([]chan json.RawMessage, 3)
	ids := []string{"first", "second", "third"}
	for i, id := range ids {
		responses[i] = make(chan json.RawMessage, 1)
		b.sendRequestToUnity(id, queueBody("scene.write"), responses[i])
	}
	conn, decoder := queueBackend(t, b)
	for i, id := range ids {
		var msg UnityMessage
		if err := decoder.Decode(&msg); err != nil {
			t.Fatal(err)
		}
		if msg.ID != id || bytes.Contains(msg.Payload, []byte("__mcpReplay")) {
			t.Fatalf("dispatch %d = %+v", i, msg)
		}
		// net.Pipe has no send buffer: this response must be consumed before
		// we read the next request, exercising bidirectional queue draining.
		if err := json.NewEncoder(conn).Encode(UnityMessage{Type: "response", ID: id,
			Payload: json.RawMessage(`{"jsonrpc":"2.0","id":7,"result":{}}`)}); err != nil {
			t.Fatal(err)
		}
		queueResponse(t, responses[i])
	}
}
func TestReloadCancellationRecoversSafeRequest(t *testing.T) {
	b := queueBridge()
	conn, decoder := queueBackend(t, b, "editor.ensure_compiled")
	response := make(chan json.RawMessage, 1)
	go b.sendRequestToUnity("compile", queueBody("editor.ensure_compiled"), response)
	var request UnityMessage
	if err := decoder.Decode(&request); err != nil {
		t.Fatal(err)
	}
	if err := json.NewEncoder(conn).Encode(UnityMessage{Type: "lifecycle", Event: "reloading"}); err != nil {
		t.Fatal(err)
	}
	if err := json.NewEncoder(conn).Encode(UnityMessage{Type: "response", ID: request.ID, Payload: json.RawMessage(`{"error":{"code":-32006}}`)}); err != nil {
		t.Fatal(err)
	}
	_ = conn.Close()
	waitQueue(t, b, 1)
	next, dec := queueBackend(t, b, "editor.ensure_compiled")
	if err := dec.Decode(&request); err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(request.Payload, []byte("__mcpReplay")) {
		t.Fatal("missing replay marker")
	}
	if err := json.NewEncoder(next).Encode(UnityMessage{Type: "response", ID: request.ID, Payload: json.RawMessage(`{"result":{"success":true}}`)}); err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(queueResponse(t, response), []byte(`"success":true`)) {
		t.Fatal("reload cancellation reached client")
	}
}
func TestReloadRecoveryPreservesTerminalReplies(t *testing.T) {
	for _, tc := range []struct {
		name         string
		reload, safe bool
		payload      string
	}{
		{"ordinary cancellation", false, true, `{"error":{"code":-32006}}`},
		{"unsafe cancellation", true, false, `{"error":{"code":-32006}}`},
		{"success during reload", true, true, `{"result":{"success":true}}`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			b := queueBridge()
			safe := []string{}
			if tc.safe {
				safe = append(safe, "editor.ensure_compiled")
			}
			conn, dec := queueBackend(t, b, safe...)
			response := make(chan json.RawMessage, 1)
			go b.sendRequestToUnity("terminal", queueBody("editor.ensure_compiled"), response)
			var request UnityMessage
			if err := dec.Decode(&request); err != nil {
				t.Fatal(err)
			}
			if tc.reload {
				if err := json.NewEncoder(conn).Encode(UnityMessage{Type: "lifecycle", Event: "reloading"}); err != nil {
					t.Fatal(err)
				}
			}
			if err := json.NewEncoder(conn).Encode(UnityMessage{Type: "response", ID: request.ID, Payload: json.RawMessage(tc.payload)}); err != nil {
				t.Fatal(err)
			}
			if string(queueResponse(t, response)) != tc.payload {
				t.Fatal("terminal reply changed")
			}
		})
	}
}
