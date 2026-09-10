# LittleBrush Unity MCP Bridge

Native proxy that keeps MCP client connections alive across Unity domain reloads, with an optional stdio launcher for on-demand project access.

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

- Source: the tracked Go files, `gateway.json`, and `go.mod` in this plugin revision
- Go: `1.26.2`
- Build: `CGO_ENABLED=0`, `-trimpath`, `-buildvcs=false`
- Authenticode: unsigned

The checksum is stored in [`mcp-bridge.exe.sha256`](mcp-bridge.exe.sha256); CI rebuilds and compares the executable byte for byte.

## Usage

```text
mcp-bridge [options]

--port          HTTP port for MCP clients (default: 48765)
--unity-port    TCP port for Unity connection (default: 48766)
--log           Log level: debug/info/warn/error (default: info)
--stdio         Serve the two MCP gateways over stdio; start Unity only on tool requests
--project       Exact project directory (required with --stdio)
--editor        Optional Editor executable; otherwise resolve the installed ProjectVersion
--idle-timeout  Managed worker idle timeout (default: 5m)
--startup-timeout  Maximum worker startup wait (default: 10m)
```

## Architecture

On-demand setup uses a stdio instance of this same binary in front of the existing HTTP bridge. Process startup is serialized by an OS file lock under the project's `Logs/`; project ownership is checked before connecting. A native bridge left behind by Unity CLI can be reused after its old Editor process exits. The launcher does not replay failed writes or terminate Editors on client disconnect. See [consumer integration](../docs/consumer-integration.md#automatic-startup-and-unity-cli) for setup, permissions, lifecycle, and limitations.

```text
[MCP client] --HTTP:48765--> [Bridge] --TCP:48766--> [Unity]
```

See [`../docs/bridge-protocol.md`](../docs/bridge-protocol.md) for the handshake, buffering, and reconnect contract.

## Verification

Run `go test ./...` and `go vet ./...`. Queue regression tests cover fresh writes
during reload, FIFO dispatch with concurrent responses, cancellation, expiry,
connection replacement, and unknown outcomes after failed writes. Use
`go test -race ./...` where a supported C compiler and CGO are available.
