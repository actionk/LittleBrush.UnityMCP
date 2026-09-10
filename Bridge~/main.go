package main

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

const (
	maxRequestBodySize    = 1 << 20 // 1 MB
	maxSessions           = 64
	sessionIdleTimeout    = 30 * time.Minute
	unityWriteTimeout     = 10 * time.Second // Per-write deadline on the Unity socket (avoids HTTP handlers hanging if the TCP send buffer fills).
	defaultBufferTimeout  = 60 * time.Second // Max time to buffer requests during reload.
	defaultRequestTimeout = 6 * time.Minute  // Must outlive the five-minute human approval window and Unity's tool timeout.
)

type LogLevel int

const (
	LogDebug LogLevel = iota
	LogInfo
	LogWarn
	LogError
)

var logLevel LogLevel

func newID() string {
	var value [16]byte
	if _, err := rand.Read(value[:]); err != nil {
		panic(fmt.Errorf("generate random ID: %w", err))
	}
	return hex.EncodeToString(value[:])
}

func logDebug(format string, args ...interface{}) {
	if logLevel <= LogDebug {
		log.Printf("[DEBUG] "+format, args...)
	}
}

func logInfo(format string, args ...interface{}) {
	if logLevel <= LogInfo {
		log.Printf("[INFO] "+format, args...)
	}
}

func logWarn(format string, args ...interface{}) {
	if logLevel <= LogWarn {
		log.Printf("[WARN] "+format, args...)
	}
}

func logError(format string, args ...interface{}) {
	if logLevel <= LogError {
		log.Printf("[ERROR] "+format, args...)
	}
}

// BridgeState represents the current state of the bridge
type BridgeState int

const (
	StateWaitingUnity BridgeState = iota
	StateReady
	StateBuffering
)

func (s BridgeState) String() string {
	switch s {
	case StateWaitingUnity:
		return "waiting_for_unity"
	case StateReady:
		return "ready"
	case StateBuffering:
		return "buffering"
	default:
		return "unknown"
	}
}

// BufferedRequest holds a request waiting to be sent to Unity
type BufferedRequest struct {
	ID        string
	Body      []byte
	RespChan  chan json.RawMessage
	CreatedAt time.Time
	// Replay is true only after a write was attempted. Waiting for the first
	// dispatch must not require the tool to support repeated execution.
	Replay bool
}

// PendingRequest holds an in-flight request sent to Unity
type PendingRequest struct {
	ID       string
	Body     []byte
	RespChan chan json.RawMessage
	// ConnID stamps which Unity connection the request was written to. On
	// reconnect we only migrate pending entries that belong to the dying conn,
	// so a late write that lost the writer race doesn't ping-pong through
	// buffered twice.
	ConnID uint64
}

// Bridge manages connections between MCP clients and Unity
type Bridge struct {
	// mu guards the bridge state (connection fields, pending/buffered maps, state).
	// Held briefly so disconnect detection isn't blocked by slow writes.
	mu             sync.Mutex
	state          BridgeState
	unityConn      net.Conn
	unityWriter    *bufio.Writer
	unityReader    *bufio.Reader
	unityConnID    uint64                     // Incremented on each new connection to detect stale goroutines
	unityReloading bool                       // A lifecycle reload pauses dispatch even before the socket closes.
	readerDone     chan struct{}              // Closed when the current readUnityMessages goroutine returns
	pending        map[string]*PendingRequest // requests sent to Unity, waiting for response
	buffered       []*BufferedRequest         // requests queued during reload

	// writeMu serializes writes to the current unityWriter. Held independently of mu
	// so that a blocked Flush (TCP send buffer full) can't stall disconnect detection.
	writeMu sync.Mutex

	// SSE notification subscribers
	sseMu           sync.Mutex
	sessions        map[string]chan []byte // sessionID -> persistent event queue
	sessionLastSeen map[string]time.Time
	sseConns        map[string]chan []byte // sessionID -> active SSE stream
	sseDropped      uint64
	sseDropReportAt time.Time

	// reloadSafeMu guards the authoritative tool policy supplied by Unity.
	reloadSafeMu    sync.RWMutex
	reloadSafeTools map[string]struct{}

	bufferTimeout  time.Duration
	requestTimeout time.Duration
	bufferMax      int

	activeProjectPath string
	activeProjectHash string
	activeProcessID   int

	// Rate limiter: token bucket
	rateMu     sync.Mutex
	rateTokens float64
	rateMax    float64
	rateRefill float64 // tokens per second
	rateLast   time.Time
}

// Message types for Unity communication
type UnityMessage struct {
	Type    string          `json:"type"`
	ID      string          `json:"id,omitempty"`
	Payload json.RawMessage `json:"payload,omitempty"`
	Event   string          `json:"event,omitempty"`
}

type UnityHello struct {
	Type            string   `json:"type"`
	Version         string   `json:"version"`
	ProjectPath     string   `json:"projectPath"`
	ProjectHash     string   `json:"projectHash"`
	ProcessID       int      `json:"processId"`
	ReloadSafeTools []string `json:"reloadSafeTools"`
}

func main() {
	stdio := flag.Bool("stdio", false, "Serve MCP over stdio and start this project's Editor on demand")
	project := flag.String("project", "", "Unity project directory (required with --stdio)")
	editor := flag.String("editor", "", "Unity Editor executable; otherwise locate the exact ProjectVersion in Unity Hub")
	idle := flag.Duration("idle-timeout", 5*time.Minute, "Managed worker idle timeout")
	startup := flag.Duration("startup-timeout", 10*time.Minute, "Maximum worker startup wait")
	port := flag.Int("port", 48765, "HTTP port for MCP clients")
	unityPort := flag.Int("unity-port", 48766, "TCP port for Unity connection")
	logLevelStr := flag.String("log", "info", "Log level: debug/info/warn/error")
	bufferTimeout := flag.Duration("buffer-timeout", defaultBufferTimeout, "Max time to buffer requests during reload")
	requestTimeout := flag.Duration("request-timeout", defaultRequestTimeout, "Max time to wait for Unity response")
	bufferMax := flag.Int("buffer-max", 100, "Max buffered requests during reload")
	rateLimit := flag.Float64("rate-limit", 60, "Max requests per second (0 = unlimited)")
	flag.Parse()
	if *stdio {
		if err := runStdio(*project, *editor, *port, *unityPort, *idle, *startup, *requestTimeout, os.Stdin, os.Stdout); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}

	// Log to file next to the executable. Append mode so we keep history across
	// bridge restarts (critical for debugging intermittent disconnects). Rotate
	// once the file exceeds a cap by renaming to .log.prev so we don't grow
	// without bound.
	exePath, _ := os.Executable()
	logPath := filepath.Join(filepath.Dir(exePath), "mcp-bridge.log")
	const maxLogBytes = 5 * 1024 * 1024 // 5 MB
	if fi, err := os.Stat(logPath); err == nil && fi.Size() > maxLogBytes {
		_ = os.Rename(logPath, logPath+".prev")
	}
	logFile, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err == nil {
		log.SetOutput(logFile)
	}

	switch strings.ToLower(*logLevelStr) {
	case "debug":
		logLevel = LogDebug
	case "info":
		logLevel = LogInfo
	case "warn":
		logLevel = LogWarn
	case "error":
		logLevel = LogError
	default:
		logLevel = LogInfo
	}

	bridge := &Bridge{
		state:           StateWaitingUnity,
		pending:         make(map[string]*PendingRequest),
		buffered:        make([]*BufferedRequest, 0),
		sessions:        make(map[string]chan []byte),
		sessionLastSeen: make(map[string]time.Time),
		sseConns:        make(map[string]chan []byte),
		bufferTimeout:   *bufferTimeout,
		requestTimeout:  *requestTimeout,
		bufferMax:       *bufferMax,
		rateTokens:      *rateLimit,
		rateMax:         *rateLimit,
		rateRefill:      *rateLimit,
		rateLast:        time.Now(),
	}

	// Start Unity listener in background
	go bridge.listenUnity(*unityPort)

	// Periodic sweeper evicts buffered entries older than bufferTimeout. Without
	// this, a prolonged Unity outage leaves stale requests sitting in buffer for
	// the full HTTP-handler totalTimeout (bufferTimeout+requestTimeout), and they
	// eventually replay against a fresh Unity producing surprising side-effects.
	go bridge.sweepBuffered()
	go bridge.sweepSessions()

	// Set up HTTP routes
	mux := http.NewServeMux()
	mux.HandleFunc("/mcp/health", bridge.handleHealth)
	mux.HandleFunc("/mcp/sse", bridge.handleSse)
	mux.HandleFunc("/mcp", bridge.handleMcp)

	logInfo("MCP Bridge listening on :%d (clients), :%d (unity)", *port, *unityPort)

	if err := http.ListenAndServe(fmt.Sprintf("127.0.0.1:%d", *port), originGuard(mux)); err != nil {
		log.Fatalf("HTTP server failed: %v", err)
	}
}

func originGuard(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !isAllowedOrigin(r.Header.Get("Origin")) {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusForbidden)
			json.NewEncoder(w).Encode(map[string]interface{}{
				"jsonrpc": "2.0",
				"id":      nil,
				"error": map[string]interface{}{
					"code":    -32000,
					"message": "Forbidden Origin",
					"data":    map[string]string{"bridgeError": "FORBIDDEN_ORIGIN"},
				},
			})
			return
		}
		next.ServeHTTP(w, r)
	})
}

func isAllowedOrigin(origin string) bool {
	if origin == "" {
		return true
	}
	u, err := url.Parse(origin)
	if err != nil {
		return false
	}
	host := strings.ToLower(u.Hostname())
	return host == "localhost" || host == "127.0.0.1" || host == "::1"
}

func (b *Bridge) listenUnity(port int) {
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		log.Fatalf("Failed to listen on Unity port %d: %v", port, err)
	}

	for {
		conn, err := ln.Accept()
		if err != nil {
			logError("Accept failed: %v", err)
			continue
		}
		b.handleUnityConnection(conn)
	}
}

func (b *Bridge) handleUnityConnection(conn net.Conn) {
	// Enable TCP keepalive to prevent idle disconnects
	if tcpConn, ok := conn.(*net.TCPConn); ok {
		tcpConn.SetKeepAlive(true)
		tcpConn.SetKeepAlivePeriod(30 * time.Second)
	}

	// Create reader/writer for this specific connection BEFORE locking
	// so the goroutine uses the correct connection's streams
	reader := bufio.NewReader(conn)
	writer := bufio.NewWriter(conn)

	// Handle handshake in goroutine - reader/writer are bound to this specific connection.
	// Connection is only published after Unity hello is validated and accepted.
	go b.handleUnityHandshake(conn, reader, writer)
}

func (b *Bridge) handleUnityHandshake(conn net.Conn, reader *bufio.Reader, writer *bufio.Writer) {
	// Read hello (reader/writer are bound to this specific connection)
	line, err := reader.ReadString('\n')
	if err != nil {
		logError("Failed to read hello from Unity: %v", err)
		_ = conn.Close()
		return
	}

	logDebug("Unity hello: %s", line)

	var hello UnityHello
	if err := json.Unmarshal([]byte(line), &hello); err != nil {
		logError("Failed to parse hello: %v", err)
		sendHandshakeError(conn, writer, "Failed to parse hello")
		return
	}

	if hello.Type != "hello" {
		logError("Expected hello, got: %s", hello.Type)
		sendHandshakeError(conn, writer, "Expected hello")
		return
	}

	// writeMu held across the swap so any in-flight sendRequestToUnity write
	// finishes (or sees the new writer) before we publish the new connection.
	// Without this, a write started against the dying writer can succeed on a
	// doomed socket while the disconnect handler has already moved pending to
	// buffered — the buffered entry then replays on the new conn, double-
	// executing the request.
	b.writeMu.Lock()
	b.mu.Lock()
	if !b.shouldAcceptBackend(hello) {
		b.mu.Unlock()
		b.writeMu.Unlock()
		logWarn("Rejected Unity backend from %s (project hash: %s, process: %d)", conn.RemoteAddr(), hello.ProjectHash, hello.ProcessID)
		sendHandshakeError(conn, writer, "Rejected Unity backend from different process")
		return
	}

	oldConn := b.unityConn
	oldReaderDone := b.readerDone

	// Increment connection ID so old goroutines know they're stale
	b.unityConnID++
	connID := b.unityConnID

	b.unityConn = conn
	b.unityWriter = writer
	b.unityReader = reader
	b.state = StateBuffering
	b.unityReloading = false
	b.readerDone = make(chan struct{})
	newReaderDone := b.readerDone
	b.rememberBackend(hello)
	b.mu.Unlock()
	b.writeMu.Unlock()

	// Ensure readerDone is always signaled after publish, even if welcome fails.
	defer close(newReaderDone)

	// Wait briefly for the old reader to process any in-flight response before
	// we force its pending entries to buffered. This narrows the window where
	// a response that actually completed pre-reload is lost and the request
	// replays unnecessarily. If the reader is already gone (its close moves
	// pending to buffered itself), the channel is closed and we proceed
	// immediately. If it's still reading, we wait up to 500ms — longer than
	// the typical post-reload response flush but short enough not to delay the
	// new connection noticeably. After the deadline, we move any still-pending
	// entries ourselves so replay isn't indefinitely stuck.
	if oldConn != nil {
		logInfo("Replacing Unity connection (new connection arrived)")

		// Close in background so the old reader's ReadString returns promptly.
		// The small sleep gives the old socket a chance to flush a pre-reload
		// response before shutdown — Unity may have written the response but
		// we haven't read it yet.
		go func(c net.Conn) {
			time.Sleep(200 * time.Millisecond)
			c.Close()
		}(oldConn)

		if oldReaderDone != nil {
			select {
			case <-oldReaderDone:
			case <-time.After(500 * time.Millisecond):
			}
		}
		// A replaced reader is stale and cannot migrate its own pending entries.
		// Always collect leftovers, including when that reader already exited.
		b.mu.Lock()
		b.bufferPendingLocked(connID)
		b.mu.Unlock()
	}

	logInfo("Unity connected from %s (connID: %d)", conn.RemoteAddr(), connID)
	logInfo("Unity project: %s (hash: %s)", hello.ProjectPath, hello.ProjectHash)

	// Count buffered requests
	b.mu.Lock()
	bufferedCount := len(b.buffered)
	b.mu.Unlock()

	// Send welcome response. Use writeMu (not state mu) so a blocked flush can't
	// stall the disconnect handler.
	welcome := map[string]interface{}{
		"type":             "welcome",
		"bridgeVersion":    "1.0",
		"bufferedRequests": bufferedCount,
	}
	welcomeBytes, _ := json.Marshal(welcome)

	b.writeMu.Lock()
	_ = conn.SetWriteDeadline(time.Now().Add(unityWriteTimeout))
	_, werr := writer.Write(welcomeBytes)
	if werr == nil {
		werr = writer.WriteByte('\n')
	}
	if werr == nil {
		werr = writer.Flush()
	}
	_ = conn.SetWriteDeadline(time.Time{})
	b.writeMu.Unlock()

	if werr != nil {
		logError("Failed to send welcome to Unity: %v", werr)
		b.abortHandshake(connID, conn)
		return
	}

	// Read responses and reload events while draining, rather than waiting for
	// the entire queue to be sent (which can deadlock a bidirectional connection).
	readDone := make(chan struct{})
	go func() {
		defer close(readDone)
		b.readUnityMessages(connID, reader)
	}()

	// Drain buffered requests in order before flipping state to Ready. Any client
	// requests that arrive mid-drain go to b.buffered (state is still Buffering)
	// and are picked up by the next iteration. This guarantees wire order:
	// replayed batch → mid-drain arrivals → post-Ready live traffic. Flipping
	// state to Ready first would let new requests race with replay writes.
	totalReplayed := 0
	for {
		b.mu.Lock()
		if b.unityConnID != connID || b.unityWriter == nil || b.unityReloading {
			b.mu.Unlock()
			break
		}
		if len(b.buffered) == 0 {
			b.state = StateReady
			b.mu.Unlock()
			break
		}
		req := b.buffered[0]
		b.mu.Unlock()

		// Keep this entry visible to cancellation/expiry until dispatch claims it
		// under the same lock that registers the pending request.
		if !b.dispatchRequest(req, true, connID) {
			break
		}
		totalReplayed++
	}

	if totalReplayed > 0 {
		logInfo("Processed %d buffered requests after Unity handshake", totalReplayed)
	} else {
		logInfo("Sent welcome to Unity (state: ready)")
	}

	<-readDone
}

func (b *Bridge) shouldAcceptBackend(hello UnityHello) bool {
	if b.unityConn != nil {
		return hello.ProcessID != 0 && hello.ProcessID == b.activeProcessID
	}
	if b.activeProcessID == 0 && b.activeProjectHash == "" && b.activeProjectPath == "" {
		return true
	}
	if b.activeProjectHash != "" && hello.ProjectHash != "" {
		return hello.ProjectHash == b.activeProjectHash
	}
	return b.activeProjectPath != "" && hello.ProjectPath == b.activeProjectPath
}

func (b *Bridge) rememberBackend(hello UnityHello) {
	b.activeProjectPath = hello.ProjectPath
	b.activeProjectHash = hello.ProjectHash
	b.activeProcessID = hello.ProcessID

	next := make(map[string]struct{}, len(hello.ReloadSafeTools))
	for _, name := range hello.ReloadSafeTools {
		if name != "" {
			next[name] = struct{}{}
		}
	}
	b.reloadSafeMu.Lock()
	b.reloadSafeTools = next
	b.reloadSafeMu.Unlock()
}

// sweepBuffered runs forever, periodically evicting buffered entries older than
// bufferTimeout. Each evicted entry gets a BUFFER_TIMEOUT error on its respCh so
// the HTTP handler unblocks immediately instead of waiting out its totalTimeout.
func (b *Bridge) sweepBuffered() {
	tick := b.bufferTimeout / 4
	if tick < 500*time.Millisecond {
		tick = 500 * time.Millisecond
	}
	for {
		time.Sleep(tick)
		b.evictAgedBuffered()
	}
}

// evictAgedBuffered removes buffered entries older than bufferTimeout and sends a
// synthetic timeout error to each of their respChans. Also called eagerly when
// the buffer fills up so a few truly-stale entries don't cause legitimate new
// requests to fail with BUFFER_FULL.
func (b *Bridge) evictAgedBuffered() int {
	b.mu.Lock()
	if len(b.buffered) == 0 {
		b.mu.Unlock()
		return 0
	}
	cutoff := time.Now().Add(-b.bufferTimeout)
	kept := b.buffered[:0]
	var evicted []*BufferedRequest
	for _, r := range b.buffered {
		if r.CreatedAt.Before(cutoff) {
			evicted = append(evicted, r)
		} else {
			kept = append(kept, r)
		}
	}
	b.buffered = kept
	b.mu.Unlock()

	for _, r := range evicted {
		errPayload, _ := json.Marshal(map[string]interface{}{
			"jsonrpc": "2.0",
			"id":      extractJsonRpcID(r.Body),
			"error": map[string]interface{}{
				"code":    -32000,
				"message": "Buffered request expired (Unity unreachable)",
				"data":    map[string]string{"bridgeError": "BUFFER_TIMEOUT"},
			},
		})
		select {
		case r.RespChan <- errPayload:
		default:
			// Handler already gave up (timer, cancel) — nothing to deliver.
		}
	}
	if n := len(evicted); n > 0 {
		logInfo("Evicted %d aged buffered requests", n)
	}
	return len(evicted)
}

// cancelRequest removes a request from both pending and buffered by its internal
// reqID. Used when the client-side timeout fires or the client HTTP connection
// drops: we need to evict the buffered entry too, otherwise a later Unity
// reconnect would replay the call and produce side-effects for a request the
// client has already given up on. If the request was in pending on a live
// connection, also send a cancel message to Unity so the in-flight tool handler
// can abort — without this, Unity keeps executing (and producing side-effects)
// long after the bridge has given up.
func (b *Bridge) cancelRequest(reqID string) {
	b.mu.Lock()
	wasPending := false
	if _, ok := b.pending[reqID]; ok {
		wasPending = true
		delete(b.pending, reqID)
	}
	for i, r := range b.buffered {
		if r.ID == reqID {
			b.buffered = append(b.buffered[:i], b.buffered[i+1:]...)
			break
		}
	}
	writer := b.unityWriter
	conn := b.unityConn
	b.mu.Unlock()

	if !wasPending || writer == nil {
		return
	}

	// Best-effort cancel notification. If it fails we don't care — the Unity
	// handler will eventually complete on its own and the orphaned response
	// will be dropped at readUnityMessages with "unknown request ID".
	msg := UnityMessage{Type: "cancel", ID: reqID}
	msgBytes, err := json.Marshal(msg)
	if err != nil {
		return
	}

	b.writeMu.Lock()
	defer b.writeMu.Unlock()
	b.mu.Lock()
	currentWriter := b.unityWriter
	b.mu.Unlock()
	if currentWriter != writer {
		// Connection swapped during our mu release → whatever was running has
		// already been torn down by the reload; no cancel needed.
		return
	}
	if conn != nil {
		_ = conn.SetWriteDeadline(time.Now().Add(unityWriteTimeout))
	}
	if _, werr := writer.Write(msgBytes); werr == nil {
		if werr = writer.WriteByte('\n'); werr == nil {
			_ = writer.Flush()
		}
	}
	if conn != nil {
		_ = conn.SetWriteDeadline(time.Time{})
	}
}

// abortHandshake clears the current Unity connection fields (if this connID is
// still the active one) and closes the socket. Called when the hello/welcome
// exchange fails — without this, b.unityWriter would keep pointing at a dead
// connection and every subsequent client request would waste time writing
// into the void before timing out.
func (b *Bridge) abortHandshake(connID uint64, conn net.Conn) {
	b.mu.Lock()
	if b.unityConnID == connID {
		b.unityConn = nil
		b.unityWriter = nil
		b.unityReader = nil
	}
	b.mu.Unlock()
	_ = conn.Close()
}

func sendHandshakeError(conn net.Conn, writer *bufio.Writer, message string) {
	payload, _ := json.Marshal(map[string]string{
		"type":    "error",
		"message": message,
	})
	_ = conn.SetWriteDeadline(time.Now().Add(unityWriteTimeout))
	_, werr := writer.Write(payload)
	if werr == nil {
		werr = writer.WriteByte('\n')
	}
	if werr == nil {
		_ = writer.Flush()
	}
	_ = conn.SetWriteDeadline(time.Time{})
	_ = conn.Close()
}

// Called with mu held. Only requests that could have reached an older connection
// need replay authorization; fresh queued work is untouched.
func (b *Bridge) bufferPendingLocked(beforeConnID uint64) {
	for id, req := range b.pending {
		if req.ConnID >= beforeConnID {
			continue
		}
		delete(b.pending, id)
		if b.shouldRejectReplay(req.Body) {
			select {
			case req.RespChan <- replayRejectedPayload(req.Body):
			default:
			}
			continue
		}
		b.buffered = append(b.buffered, &BufferedRequest{
			ID: id, Body: req.Body, RespChan: req.RespChan, CreatedAt: time.Now(), Replay: true,
		})
	}
}

func (b *Bridge) readUnityMessages(connID uint64, reader *bufio.Reader) {
	logDebug("readUnityMessages started (connID: %d)", connID)

	for {
		logDebug("Waiting for Unity message (connID: %d)...", connID)
		line, err := reader.ReadString('\n')
		if err != nil {
			logInfo("Unity read returned error (connID: %d): %v", connID, err)

			b.mu.Lock()
			// Only clear state if this is still the current connection
			// (prevents stale goroutine from clearing new connection's state)
			if b.unityConnID == connID {
				logInfo("Unity disconnected (connID: %d)", connID)
				b.unityConn = nil
				b.unityWriter = nil
				b.unityReader = nil

				// Any disconnect — graceful reload or unexpected drop — is treated
				// as "Unity might be coming back". Always move pending to buffered
				// here so they replay on reconnect. The lifecycle:reloading handler
				// only flips state and intentionally leaves pending in place (so a
				// response that completed pre-reload can still be delivered), which
				// means this is the single chokepoint that moves them.
				b.state = StateBuffering
				b.bufferPendingLocked(connID + 1)
			} else {
				logInfo("Stale Unity connection closed (connID: %d, current: %d)", connID, b.unityConnID)
			}

			b.mu.Unlock()
			return
		}

		logDebug("Unity message: %s", line)

		var msg UnityMessage
		if err := json.Unmarshal([]byte(line), &msg); err != nil {
			logWarn("Failed to parse Unity message: %v", err)
			continue
		}

		switch msg.Type {
		case "response":
			// Look up the request in pending first, then in buffered. The
			// buffered lookup handles a subtle rapid-reconnect case: if Unity
			// executed the request and sent a response on an old connection,
			// but handleUnityConnection for a newer connection already moved
			// the entry pending -> buffered (assuming the old call was lost),
			// we'd otherwise replay on the new conn and double-execute. By
			// delivering the old response and dropping the buffered entry we
			// avoid that.
			b.mu.Lock()
			var respCh chan json.RawMessage
			if p, ok := b.pending[msg.ID]; ok {
				// Reload teardown cancels handlers before closing the socket. Keep
				// safe requests pending so disconnect recovery can replay them.
				var reply struct {
					Error *struct {
						Code int `json:"code"`
					} `json:"error"`
				}
				if b.unityReloading && p.ConnID == connID && b.isReloadSafeRequest(p.Body) &&
					json.Unmarshal(msg.Payload, &reply) == nil && reply.Error != nil && reply.Error.Code == -32006 {
					b.mu.Unlock()
					continue
				}
				respCh = p.RespChan
				delete(b.pending, msg.ID)
			} else {
				for i, br := range b.buffered {
					if br.ID == msg.ID {
						respCh = br.RespChan
						b.buffered = append(b.buffered[:i], b.buffered[i+1:]...)
						logInfo("Response for buffered request %s arrived on stale conn; cancelling replay", msg.ID)
						break
					}
				}
			}
			b.mu.Unlock()

			if respCh != nil {
				// Non-blocking send. RespChan has capacity 1; if somehow
				// already full, drop the dup rather than parking this reader
				// goroutine forever.
				select {
				case respCh <- msg.Payload:
				default:
					logWarn("Dropping duplicate response for request %s", msg.ID)
				}
			} else {
				logWarn("Received response for unknown request ID: %s", msg.ID)
			}

		case "notification":
			b.broadcastSse(msg.Payload)
			logDebug("Notification broadcast: %s", string(msg.Payload))

		case "lifecycle":
			b.handleLifecycleEvent(msg.Event, connID)

		default:
			logWarn("Unknown message type: %s", msg.Type)
		}
	}
}

func (b *Bridge) handleLifecycleEvent(event string, connID uint64) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.unityConnID != connID {
		return
	}
	logInfo("Unity lifecycle event: %s", event)

	switch event {
	case "reloading":
		b.state = StateBuffering
		b.unityReloading = true
		// Intentionally do NOT move pending -> buffered here. Unity may still
		// complete an in-flight tool call between "reloading" and the actual
		// socket close; keeping pending intact lets that response be delivered.
		// The disconnect handler (readUnityMessages) takes care of the move
		// once the socket actually drops, so we don't lose any requests either.
		pendingCount := len(b.pending)
		logInfo("Entering buffering mode (Unity reloading), %d in-flight requests kept in pending", pendingCount)

	case "shutdown":
		logInfo("Unity shutting down")
		// Unity is exiting, not reloading. The bridge is launched as a detached
		// process so it survives domain reloads, which means it must explicitly
		// exit on editor shutdown instead of waiting for parent-process cleanup.
		go func() {
			time.Sleep(100 * time.Millisecond)
			logInfo("MCP bridge exiting after Unity shutdown")
			os.Exit(0)
		}()
	}
}

func (b *Bridge) handleHealth(w http.ResponseWriter, r *http.Request) {
	b.mu.Lock()
	state := b.state
	connected := b.unityConn != nil
	projectPath := b.activeProjectPath
	projectHash := b.activeProjectHash
	processID := b.activeProcessID
	connID := b.unityConnID
	pendingCount := len(b.pending)
	bufferedCount := len(b.buffered)
	b.mu.Unlock()

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"ok":               true,
		"bridgeProcessId":  os.Getpid(),
		"bridgeVersion":    "1.0",
		"unityConnected":   connected,
		"state":            state.String(),
		"projectPath":      projectPath,
		"projectHash":      projectHash,
		"processId":        processID,
		"connID":           connID,
		"pendingRequests":  pendingCount,
		"bufferedRequests": bufferedCount,
	})
}

// allowRequest checks the token bucket rate limiter. Returns false if rate exceeded.
func (b *Bridge) allowRequest() bool {
	if b.rateMax <= 0 {
		return true // unlimited
	}
	b.rateMu.Lock()
	defer b.rateMu.Unlock()

	now := time.Now()
	elapsed := now.Sub(b.rateLast).Seconds()
	b.rateLast = now
	b.rateTokens += elapsed * b.rateRefill
	if b.rateTokens > b.rateMax {
		b.rateTokens = b.rateMax
	}
	if b.rateTokens < 1 {
		return false
	}
	b.rateTokens--
	return true
}

// extractJsonRpcID pulls the "id" field from a JSON-RPC request body for use in error responses.
func extractJsonRpcID(body []byte) interface{} {
	var envelope struct {
		ID json.RawMessage `json:"id"`
	}
	if json.Unmarshal(body, &envelope) == nil && len(envelope.ID) > 0 {
		var parsed interface{}
		if json.Unmarshal(envelope.ID, &parsed) == nil {
			return parsed
		}
	}
	return nil
}

func extractToolName(body []byte) string {
	var envelope struct {
		Method string `json:"method"`
		Params struct {
			Name      string          `json:"name"`
			Arguments json.RawMessage `json:"arguments"`
		} `json:"params"`
	}
	if json.Unmarshal(body, &envelope) != nil {
		return ""
	}
	if envelope.Method == "tools/call" {
		if envelope.Params.Name == "unity.call" {
			var gateway struct {
				Tool string `json:"tool"`
			}
			if json.Unmarshal(envelope.Params.Arguments, &gateway) == nil {
				return gateway.Tool
			}
		}
		return envelope.Params.Name
	}
	return envelope.Method
}

func markBridgeSessionPayload(body []byte, sessionID string) []byte {
	var root map[string]interface{}
	if sessionID == "" || json.Unmarshal(body, &root) != nil || root["method"] != "tools/call" {
		return body
	}
	params, ok := root["params"].(map[string]interface{})
	if !ok {
		return body
	}
	meta, ok := params["_meta"].(map[string]interface{})
	if !ok || meta == nil {
		meta = map[string]interface{}{}
		params["_meta"] = meta
	}
	meta["bridgeSessionId"] = sessionID
	marked, err := json.Marshal(root)
	if err != nil {
		return body
	}
	return marked
}

func (b *Bridge) shouldRejectReplay(body []byte) bool {
	return !b.isReloadSafeRequest(body)
}

func (b *Bridge) isReloadSafeRequest(body []byte) bool {
	name := extractToolName(body)
	switch name {
	case "initialize", "ping", "tools/list":
		return true
	}
	b.reloadSafeMu.RLock()
	_, ok := b.reloadSafeTools[name]
	b.reloadSafeMu.RUnlock()
	return ok
}

func markReplayPayload(body []byte) []byte {
	var root map[string]interface{}
	if json.Unmarshal(body, &root) != nil {
		return body
	}
	params, ok := root["params"].(map[string]interface{})
	if !ok {
		return body
	}
	args, ok := params["arguments"].(map[string]interface{})
	if !ok || args == nil {
		args = map[string]interface{}{}
		params["arguments"] = args
	}
	targetArgs := args
	if params["name"] == "unity.call" {
		var nestedOk bool
		targetArgs, nestedOk = args["arguments"].(map[string]interface{})
		if !nestedOk || targetArgs == nil {
			targetArgs = map[string]interface{}{}
			args["arguments"] = targetArgs
		}
	}
	targetArgs["__mcpReplay"] = true
	marked, err := json.Marshal(root)
	if err != nil {
		return body
	}
	return marked
}

func replayRejectedPayload(body []byte) json.RawMessage {
	name := extractToolName(body)
	if name == "" {
		name = "request"
	}
	payload, _ := json.Marshal(map[string]interface{}{
		"jsonrpc": "2.0",
		"id":      extractJsonRpcID(body),
		"error": map[string]interface{}{
			"code":    -32000,
			"message": fmt.Sprintf("%s was dispatched to Unity but its result could not be confirmed; automatic replay is disabled", name),
			"data":    map[string]string{"bridgeError": "REPLAY_REJECTED", "tool": name, "executionState": "unknown"},
		},
	})
	return payload
}

func shouldAcceptWithoutResponse(body []byte) bool {
	var msg map[string]json.RawMessage
	if err := json.Unmarshal(body, &msg); err != nil {
		return false
	}

	_, hasID := msg["id"]
	_, hasMethod := msg["method"]
	if hasMethod && !hasID {
		return true
	}

	if !hasMethod {
		_, hasResult := msg["result"]
		_, hasError := msg["error"]
		return hasResult || hasError
	}

	return false
}

func (b *Bridge) handleCancellationNotification(body []byte) {
	var notification struct {
		Method string `json:"method"`
		Params struct {
			RequestID json.RawMessage `json:"requestId"`
		} `json:"params"`
	}
	if json.Unmarshal(body, &notification) != nil || notification.Method != "notifications/cancelled" || len(notification.Params.RequestID) == 0 {
		return
	}

	wanted := compactJSON(notification.Params.RequestID)
	b.mu.Lock()
	var matches []string
	for id, request := range b.pending {
		if bytes.Equal(compactJSON(rawRequestID(request.Body)), wanted) {
			matches = append(matches, id)
		}
	}
	for _, request := range b.buffered {
		if bytes.Equal(compactJSON(rawRequestID(request.Body)), wanted) {
			matches = append(matches, request.ID)
		}
	}
	b.mu.Unlock()

	for _, id := range matches {
		b.cancelRequest(id)
	}
}

func rawRequestID(body []byte) json.RawMessage {
	var request struct {
		ID json.RawMessage `json:"id"`
	}
	_ = json.Unmarshal(body, &request)
	return request.ID
}

func compactJSON(value []byte) []byte {
	var compact bytes.Buffer
	if json.Compact(&compact, value) != nil {
		return nil
	}
	return compact.Bytes()
}

func (b *Bridge) newSession() (string, bool) {
	sessionID := newID()
	ch := make(chan []byte, 64)
	now := time.Now()

	b.sseMu.Lock()
	if b.sessions == nil {
		b.sessions = make(map[string]chan []byte)
	}
	if b.sessionLastSeen == nil {
		b.sessionLastSeen = make(map[string]time.Time)
	}
	b.evictExpiredSessionsLocked(now)
	if len(b.sessions) >= maxSessions {
		b.sseMu.Unlock()
		return "", false
	}
	b.sessions[sessionID] = ch
	b.sessionLastSeen[sessionID] = now
	b.sseMu.Unlock()

	return sessionID, true
}

func (b *Bridge) sessionChannel(sessionID string) (chan []byte, bool) {
	b.sseMu.Lock()
	defer b.sseMu.Unlock()
	ch, ok := b.sessions[sessionID]
	if ok {
		if b.sessionLastSeen == nil {
			b.sessionLastSeen = make(map[string]time.Time)
		}
		b.sessionLastSeen[sessionID] = time.Now()
	}
	return ch, ok
}

func (b *Bridge) removeSession(sessionID string) bool {
	b.sseMu.Lock()
	defer b.sseMu.Unlock()

	_, ok := b.sessions[sessionID]
	if !ok {
		return false
	}
	b.removeSessionLocked(sessionID)
	return true
}

func (b *Bridge) removeSessionLocked(sessionID string) {
	ch := b.sessions[sessionID]
	delete(b.sessions, sessionID)
	delete(b.sessionLastSeen, sessionID)
	delete(b.sseConns, sessionID)
	if ch != nil {
		close(ch)
	}
}

func (b *Bridge) evictExpiredSessionsLocked(now time.Time) int {
	removed := 0
	for id, touched := range b.sessionLastSeen {
		if _, active := b.sseConns[id]; active {
			continue
		}
		if now.Sub(touched) >= sessionIdleTimeout {
			b.removeSessionLocked(id)
			removed++
		}
	}
	return removed
}

func (b *Bridge) sweepSessions() {
	for {
		time.Sleep(time.Minute)
		b.sseMu.Lock()
		removed := b.evictExpiredSessionsLocked(time.Now())
		b.sseMu.Unlock()
		if removed > 0 {
			logInfo("Expired %d idle MCP session(s)", removed)
		}
	}
}

func isSuccessfulInitializeResponse(payload []byte) bool {
	var response struct {
		Result json.RawMessage `json:"result"`
		Error  json.RawMessage `json:"error"`
	}
	if json.Unmarshal(payload, &response) != nil {
		return false
	}
	return len(response.Result) > 0 && len(response.Error) == 0
}

func writeSessionError(w http.ResponseWriter, status int, message string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(map[string]interface{}{
		"jsonrpc": "2.0",
		"id":      nil,
		"error": map[string]interface{}{
			"code":    -32000,
			"message": message,
		},
	})
}

func (b *Bridge) writeMcpResponse(w http.ResponseWriter, payload []byte, isInitialize bool) {
	w.Header().Set("Content-Type", "application/json")
	if isInitialize && isSuccessfulInitializeResponse(payload) {
		sessionID, ok := b.newSession()
		if !ok {
			writeSessionError(w, http.StatusServiceUnavailable, "Session capacity reached")
			return
		}
		w.Header().Set("Mcp-Session-Id", sessionID)
	}
	_, _ = w.Write(payload)
}

func (b *Bridge) handleMcp(w http.ResponseWriter, r *http.Request) {
	// Only support POST for now
	if r.Method == "GET" {
		accept := r.Header.Get("Accept")
		if strings.Contains(accept, "text/event-stream") {
			b.handleSse(w, r)
			return
		}
		http.Error(w, "Use POST for JSON-RPC", http.StatusMethodNotAllowed)
		return
	}

	if r.Method == "DELETE" {
		sessionID := r.Header.Get("Mcp-Session-Id")
		if sessionID == "" {
			writeSessionError(w, http.StatusBadRequest, "Missing Mcp-Session-Id header")
			return
		}
		if !b.removeSession(sessionID) {
			writeSessionError(w, http.StatusNotFound, "Session not found")
			return
		}
		w.WriteHeader(http.StatusNoContent)
		return
	}

	if r.Method != "POST" {
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if version := r.Header.Get("MCP-Protocol-Version"); version != "" && version != "2025-06-18" {
		http.Error(w, "Unsupported MCP-Protocol-Version", http.StatusBadRequest)
		return
	}

	// Rate limit
	if !b.allowRequest() {
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Retry-After", "1")
		w.WriteHeader(http.StatusTooManyRequests)
		json.NewEncoder(w).Encode(map[string]interface{}{
			"jsonrpc": "2.0",
			"id":      nil,
			"error": map[string]interface{}{
				"code":    -32000,
				"message": "Rate limit exceeded",
				"data":    map[string]string{"bridgeError": "RATE_LIMITED"},
			},
		})
		return
	}

	// Read request body with size limit
	body, err := io.ReadAll(io.LimitReader(r.Body, maxRequestBodySize+1))
	if err != nil {
		http.Error(w, "Failed to read request body", http.StatusBadRequest)
		return
	}
	if len(body) > maxRequestBodySize {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusRequestEntityTooLarge)
		json.NewEncoder(w).Encode(map[string]interface{}{
			"jsonrpc": "2.0",
			"id":      nil,
			"error": map[string]interface{}{
				"code":    -32000,
				"message": fmt.Sprintf("Request body too large (max %d bytes)", maxRequestBodySize),
			},
		})
		return
	}

	logDebug("Client request: %s", string(body))

	// Validate JSON and reject batches/scalars before forwarding to Unity.
	var jsonCheck json.RawMessage
	if err := json.Unmarshal(body, &jsonCheck); err != nil {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		json.NewEncoder(w).Encode(map[string]interface{}{
			"jsonrpc": "2.0",
			"id":      nil,
			"error": map[string]interface{}{
				"code":    -32700,
				"message": "Parse error: invalid JSON",
			},
		})
		return
	}
	var envelope map[string]json.RawMessage
	if err := json.Unmarshal(body, &envelope); err != nil || envelope == nil {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		json.NewEncoder(w).Encode(map[string]interface{}{
			"jsonrpc": "2.0",
			"id":      nil,
			"error": map[string]interface{}{
				"code":    -32600,
				"message": "Invalid Request: expected a JSON object",
			},
		})
		return
	}

	isInitialize := extractToolName(body) == "initialize"
	sessionID := r.Header.Get("Mcp-Session-Id")
	if isInitialize {
		if sessionID != "" {
			writeSessionError(w, http.StatusBadRequest, "Initialize must not include Mcp-Session-Id")
			return
		}
	} else {
		if sessionID == "" {
			writeSessionError(w, http.StatusBadRequest, "Missing Mcp-Session-Id header")
			return
		}
		if _, ok := b.sessionChannel(sessionID); !ok {
			writeSessionError(w, http.StatusNotFound, "Session not found")
			return
		}
		body = markBridgeSessionPayload(body, sessionID)
	}

	if shouldAcceptWithoutResponse(body) {
		b.handleCancellationNotification(body)
		w.WriteHeader(http.StatusAccepted)
		return
	}

	// Extract JSON-RPC ID from request for use in error responses
	clientID := extractJsonRpcID(body)

	// Generate internal request ID and create response channel
	reqID := newID()
	respCh := make(chan json.RawMessage, 1)

	// Handle based on state
	b.mu.Lock()
	state := b.state

	switch state {
	case StateReady:
		// Normal path - send to Unity immediately
		b.mu.Unlock()
		b.sendRequestToUnity(reqID, body, respCh)

	case StateBuffering, StateWaitingUnity:
		// Unity isn't reachable right now (reloading, restarting, or hasn't
		// connected yet). Buffer the request — per-request timeout decides
		// when to give up, so clients don't see spurious failures during
		// domain reloads.
		if len(b.buffered) >= b.bufferMax {
			// Try to free space by evicting aged entries before giving up.
			// The sweeper runs on its own cadence; an eager sweep here avoids
			// BUFFER_FULL errors caused by abandoned entries still occupying
			// slots.
			b.mu.Unlock()
			b.evictAgedBuffered()
			b.mu.Lock()
			// Reconnect may have drained the queue while eviction released mu.
			// Don't strand this fresh call in a queue whose drainer has exited.
			if b.state == StateReady {
				b.mu.Unlock()
				b.sendRequestToUnity(reqID, body, respCh)
				break
			}
		}
		if len(b.buffered) >= b.bufferMax {
			b.mu.Unlock()
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusServiceUnavailable)
			json.NewEncoder(w).Encode(map[string]interface{}{
				"jsonrpc": "2.0",
				"id":      clientID,
				"error": map[string]interface{}{
					"code":    -32000,
					"message": "Buffer full, Unity not reachable",
					"data":    map[string]string{"bridgeError": "BUFFER_FULL"},
				},
			})
			return
		}

		bufferedReq := &BufferedRequest{
			ID:        reqID,
			Body:      body,
			RespChan:  respCh,
			CreatedAt: time.Now(),
		}
		b.buffered = append(b.buffered, bufferedReq)
		logInfo("Buffered request %s (state: %s, %d in queue)", reqID, state, len(b.buffered))
		b.mu.Unlock()
	}

	// Wait for response with timeout. NewTimer + Stop (rather than time.After)
	// so the timer is released immediately on fast-path success — otherwise
	// every short-lived request leaves a timer alive until totalTimeout elapses.
	totalTimeout := b.bufferTimeout + b.requestTimeout
	timer := time.NewTimer(totalTimeout)
	defer timer.Stop()
	select {
	case payload, ok := <-respCh:
		if !ok {
			// Channel closed — defensive fallback. Should not happen: the bridge no
			// longer closes respCh anywhere. If it does, treat as internal error
			// rather than UNITY_DISCONNECTED (which implies recoverable transport
			// loss; a closed channel is a bridge bug).
			logError("Response channel closed unexpectedly for %s", reqID)
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusInternalServerError)
			json.NewEncoder(w).Encode(map[string]interface{}{
				"jsonrpc": "2.0",
				"id":      clientID,
				"error": map[string]interface{}{
					"code":    -32603,
					"message": "Internal bridge error: response channel closed",
					"data":    map[string]string{"bridgeError": "INTERNAL"},
				},
			})
			return
		}

		logDebug("Response for %s: %s", reqID, string(payload))
		b.writeMcpResponse(w, payload, isInitialize)

	case <-timer.C:
		// Evict from both pending and buffered so a later Unity reconnect doesn't
		// replay a request the client has already given up on.
		b.cancelRequest(reqID)

		logWarn("Request %s timed out after %v", reqID, totalTimeout)
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusGatewayTimeout)
		json.NewEncoder(w).Encode(map[string]interface{}{
			"jsonrpc": "2.0",
			"id":      clientID,
			"error": map[string]interface{}{
				"code":    -32000,
				"message": "Request timed out",
				"data":    map[string]string{"bridgeError": "TIMEOUT"},
			},
		})

	case <-r.Context().Done():
		// MCP transport disconnects do not imply cancellation. The request remains
		// active until completion, timeout, or notifications/cancelled.
		logInfo("Client disconnected while request %s remains active", reqID)
		return
	}

	// Final cleanup: delete-on-deliver in readUnityMessages already handled the
	// success case and cancelRequest handled the timeout case, so this is
	// usually a no-op — kept as a defensive guard against any path that might
	// leave a stale entry.
	b.mu.Lock()
	delete(b.pending, reqID)
	b.mu.Unlock()
}

func (b *Bridge) sendRequestToUnity(reqID string, body []byte, respCh chan json.RawMessage) {
	b.dispatchRequest(&BufferedRequest{ID: reqID, Body: body, RespChan: respCh, CreatedAt: time.Now()}, false, 0)
}

// queued entries remain cancellable until they are atomically claimed as pending.
// false tells the reconnect drainer that its connection is no longer usable.
func (b *Bridge) dispatchRequest(req *BufferedRequest, queued bool, expectedConnID uint64) bool {
	reqID, body, respCh := req.ID, req.Body, req.RespChan
	if req.Replay {
		body = markReplayPayload(body)
	}
	msg := UnityMessage{
		Type:    "request",
		ID:      reqID,
		Payload: body,
	}
	msgBytes, err := json.Marshal(msg)
	if err != nil {
		// Near-impossible (payload is pre-validated JSON), but don't close respCh —
		// that would surface as a bogus UNITY_DISCONNECTED. Send a synthetic JSON-RPC
		// error envelope instead so the client sees the real cause.
		logError("Failed to marshal request %s: %v", reqID, err)
		errPayload, _ := json.Marshal(map[string]interface{}{
			"jsonrpc": "2.0",
			"id":      extractJsonRpcID(body),
			"error": map[string]interface{}{
				"code":    -32603,
				"message": fmt.Sprintf("Bridge failed to marshal request: %v", err),
				"data":    map[string]string{"bridgeError": "INTERNAL"},
			},
		})
		respCh <- errPayload
		return true
	}

	// Take writeMu BEFORE grabbing mu. handleUnityConnection takes writeMu
	// across the connection swap, so holding it here guarantees the writer we
	// capture is still the live one for the entire write window. Any
	// reconnect-in-progress will wait for us, or we'll wait for it and then
	// see the new writer.
	b.writeMu.Lock()
	b.mu.Lock()
	writer := b.unityWriter
	conn := b.unityConn
	connID := b.unityConnID
	queueIndex := -1
	if queued {
		if connID != expectedConnID || writer == nil || b.unityReloading {
			b.mu.Unlock()
			b.writeMu.Unlock()
			return false
		}
		for i, candidate := range b.buffered {
			if candidate == req {
				queueIndex = i
				break
			}
		}
		if queueIndex < 0 { // Cancelled, expired, or answered before dispatch.
			b.mu.Unlock()
			b.writeMu.Unlock()
			return true
		}
	}
	if queued && time.Since(req.CreatedAt) >= b.bufferTimeout {
		b.buffered = append(b.buffered[:queueIndex], b.buffered[queueIndex+1:]...)
		b.mu.Unlock()
		b.writeMu.Unlock()
		respCh <- bufferErrorPayload(body, "BUFFER_TIMEOUT", "Buffered request expired before dispatch")
		return true
	}
	if req.Replay && b.shouldRejectReplay(req.Body) {
		if queueIndex >= 0 {
			b.buffered = append(b.buffered[:queueIndex], b.buffered[queueIndex+1:]...)
		}
		b.mu.Unlock()
		b.writeMu.Unlock()
		respCh <- replayRejectedPayload(req.Body)
		return true
	}
	if writer == nil || (!queued && b.state != StateReady) {
		// No write was attempted. Queue even tools that must never be replayed.
		if len(b.buffered) >= b.bufferMax {
			b.mu.Unlock()
			b.writeMu.Unlock()
			respCh <- bufferErrorPayload(body, "BUFFER_FULL", "Buffer full, Unity not reachable")
			return false
		}
		b.buffered = append(b.buffered, req)
		if b.state == StateReady {
			b.state = StateBuffering
		}
		b.mu.Unlock()
		b.writeMu.Unlock()
		logInfo("Unity not ready, buffered request %s (replay: %t)", reqID, req.Replay)
		return false
	}
	if queueIndex >= 0 {
		b.buffered = append(b.buffered[:queueIndex], b.buffered[queueIndex+1:]...)
	}
	b.pending[reqID] = &PendingRequest{
		ID:       reqID,
		Body:     req.Body,
		RespChan: respCh,
		ConnID:   connID,
	}
	b.mu.Unlock()

	// Perform the write under writeMu with a per-write deadline. Without the
	// deadline a full TCP send buffer (hung Unity) would block Flush forever
	// and the HTTP handler would hang past its own totalTimeout because the
	// select-with-timeout isn't reached until this function returns.
	if conn != nil {
		_ = conn.SetWriteDeadline(time.Now().Add(unityWriteTimeout))
	}
	_, err = writer.Write(msgBytes)
	if err == nil {
		err = writer.WriteByte('\n')
	}
	if err == nil {
		err = writer.Flush()
	}
	if conn != nil {
		_ = conn.SetWriteDeadline(time.Time{})
	}
	b.writeMu.Unlock()

	if err != nil {
		logError("Failed to send request %s to Unity: %v", reqID, err)
		// Write failed — Unity socket is likely dying. Buffer for replay instead of
		// closing the channel. The disconnect handler will pick up the broken connection
		// shortly and the buffered request will replay on reconnect.
		b.mu.Lock()
		// Race: readUnityMessages's disconnect handler may have already moved this
		// reqID from pending to buffered while we held no lock. Only buffer here if
		// the entry is still in pending — otherwise we'd enqueue a duplicate and
		// Unity would execute the request twice on replay.
		if pending, stillPending := b.pending[reqID]; stillPending && pending.ConnID == connID {
			delete(b.pending, reqID)
			if b.shouldRejectReplay(body) {
				respCh <- replayRejectedPayload(body)
				logWarn("Rejected non-reload-safe request instead of buffering after Unity write failure")
			} else {
				b.buffered = append(b.buffered, &BufferedRequest{
					ID:        reqID,
					Body:      body,
					RespChan:  respCh,
					CreatedAt: time.Now(),
					Replay:    true,
				})
			}
		}
		if b.unityConnID == connID && b.state == StateReady {
			b.state = StateBuffering
		}
		b.mu.Unlock()
		if conn != nil {
			_ = conn.Close()
		}
		return false
	}

	logDebug("Sent request %s to Unity", reqID)
	return true
}

func bufferErrorPayload(body []byte, code, message string) json.RawMessage {
	payload, _ := json.Marshal(map[string]interface{}{
		"jsonrpc": "2.0", "id": extractJsonRpcID(body),
		"error": map[string]interface{}{
			"code": -32000, "message": message,
			"data": map[string]string{"bridgeError": code},
		},
	})
	return payload
}

// handleSse serves Server-Sent Events for MCP notifications.
func (b *Bridge) handleSse(w http.ResponseWriter, r *http.Request) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "Streaming not supported", http.StatusInternalServerError)
		return
	}

	sessionID := r.Header.Get("Mcp-Session-Id")
	b.sseMu.Lock()
	ch, exists := b.sessions[sessionID]
	if sessionID == "" || !exists {
		b.sseMu.Unlock()
		if sessionID == "" {
			writeSessionError(w, http.StatusBadRequest, "Missing Mcp-Session-Id header")
		} else {
			writeSessionError(w, http.StatusNotFound, "Session not found")
		}
		return
	}
	if _, active := b.sseConns[sessionID]; active {
		b.sseMu.Unlock()
		writeSessionError(w, http.StatusConflict, "Session already has an active SSE stream")
		return
	}
	b.sseConns[sessionID] = ch
	if b.sessionLastSeen == nil {
		b.sessionLastSeen = make(map[string]time.Time)
	}
	b.sessionLastSeen[sessionID] = time.Now()
	total := len(b.sseConns)
	b.sseMu.Unlock()

	logInfo("SSE client connected: %s (total: %d)", sessionID[:8], total)

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.WriteHeader(http.StatusOK)
	flusher.Flush()

	ctx := r.Context()
	for {
		select {
		case <-ctx.Done():
			b.sseMu.Lock()
			if active, ok := b.sseConns[sessionID]; ok && active == ch {
				delete(b.sseConns, sessionID)
			}
			b.sseMu.Unlock()
			logInfo("SSE client disconnected: %s", sessionID[:8])
			return
		case data, ok := <-ch:
			if !ok {
				return
			}
			fmt.Fprintf(w, "data: %s\n\n", data)
			flusher.Flush()
		}
	}
}

// broadcastSse sends a notification to all SSE clients.
func (b *Bridge) broadcastSse(payload json.RawMessage) {
	b.sseMu.Lock()
	defer b.sseMu.Unlock()

	if len(b.sessions) == 0 {
		return
	}

	for _, ch := range b.sessions {
		select {
		case ch <- payload:
		default:
			b.sseDropped++
		}
	}
	// Preserve bounded reconnect queues, but do not emit one log line per drop.
	if b.sseDropped > 0 {
		now := time.Now()
		if b.sseDropReportAt.IsZero() {
			b.sseDropReportAt = now
		} else if now.Sub(b.sseDropReportAt) >= time.Minute {
			logWarn("SSE queues dropped %d notifications since the previous report; RPC replies are unaffected", b.sseDropped)
			b.sseDropped = 0
			b.sseDropReportAt = now
		}
	}
}
