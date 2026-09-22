# LittleBrush Unity MCP Bridge

Native bridge that keeps MCP connections alive across domain reloads, with an on-demand stdio launcher for closed projects.

## Build

```powershell
# Current platform
go build -trimpath -buildvcs=false -o mcp-bridge.exe .

# Cross-compile
$env:CGO_ENABLED = "0"
$env:GOOS = "windows"; $env:GOARCH = "amd64"; go build -trimpath -buildvcs=false -o mcp-bridge.exe .
$env:GOOS = "darwin";  $env:GOARCH = "amd64"; go build -trimpath -buildvcs=false -o mcp-bridge-darwin-amd64 .
$env:GOOS = "darwin";  $env:GOARCH = "arm64"; go build -trimpath -buildvcs=false -o mcp-bridge-darwin-arm64 .
$env:GOOS = "linux";   $env:GOARCH = "amd64"; go build -trimpath -buildvcs=false -o mcp-bridge-linux .
```

The bridge uses only the Go standard library.

Release binaries disable embedded VCS metadata so byte-for-byte CI verification
is independent of checkout location. Record source provenance below instead.

## Bundled Windows binary

Users do not need Go for the bundled Windows/amd64 bridge.

- Source: current `main.go`, `launcher*.go`, and `gateway.json` (module definition in `go.mod`).
- Go: `1.26.2`
- Build: `CGO_ENABLED=0`, `-trimpath`, `-buildvcs=false`
- SHA-256: `A283F2F1650BC19DAAE256970B44A2CFC02237D7C37F5C189375A699063E22A4`
- Authenticode: unsigned

The checksum is also stored in [`mcp-bridge.exe.sha256`](mcp-bridge.exe.sha256).

## Usage

```text
mcp-bridge [options]

--stdio         Serve MCP over stdio; reuse the Editor or start a managed batch fallback
--project       Project directory (required with --stdio)
--editor        Optional exact Unity executable override
--idle-timeout  Managed worker idle timeout (default: 5m)
--startup-timeout Startup/import deadline (default: 10m)
--port          HTTP port for MCP clients (default: 48765)
--unity-port    TCP port for Unity connection (default: 48766)
--log           Log level: debug/info/warn/error (default: info)
--buffer-timeout Maximum request buffering during Unity reload (default: 3m)
```

## Architecture

```text
[MCP client] --HTTP:48765--> [Bridge] --TCP:48766--> [Unity]
```

See [`../docs/bridge-protocol.md`](../docs/bridge-protocol.md) for the handshake, buffering, and reconnect contract.

## Verification

Run `go test ./...` and `go vet ./...`. Queue regression tests cover fresh writes
during reload, FIFO dispatch with concurrent responses, cancellation, expiry,
connection replacement, and unknown outcomes after failed writes. Use
`go test -race ./...` where a supported C compiler and CGO are available.

For client setup and ownership rules, see [consumer integration](../docs/consumer-integration.md#automatic-startup-and-unity-cli). The launcher never replays failed writes or closes a manually opened Editor.
