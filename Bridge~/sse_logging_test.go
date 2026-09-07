package main

import (
	"encoding/json"
	"testing"
	"time"
)

func TestSseOverflowAggregatesWithoutGrowingReconnectQueue(t *testing.T) {
	queue := make(chan []byte, 1)
	queue <- []byte("retained")
	b := &Bridge{sessions: map[string]chan []byte{"session": queue}}
	for i := 0; i < 10000; i++ {
		b.broadcastSse(json.RawMessage(`{"method":"changed"}`))
	}
	if len(queue) != 1 || b.sseDropped != 10000 {
		t.Fatalf("unexpected queue/report state: queued=%d dropped=%d", len(queue), b.sseDropped)
	}
	b.sseDropReportAt = time.Now().Add(-time.Minute)
	b.broadcastSse(json.RawMessage(`{}`))
	if b.sseDropped != 0 || time.Since(b.sseDropReportAt) > time.Second {
		t.Fatal("aggregate report did not reset its bounded interval counter")
	}
	if string(<-queue) != "retained" {
		t.Fatal("overflow changed retained reconnect notification")
	}
}
