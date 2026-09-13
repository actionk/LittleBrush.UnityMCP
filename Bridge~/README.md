# LittleBrush Unity MCP Bridge

Standalone proxy that keeps MCP client connections alive across Unity domain reloads.

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

- Source: working-tree `main.go`, SHA-256 `76600BD69C61FFF703CCED4C0BED67689A880E9D2525BCD98D2CED43EBC54F7E` (module definition in `go.mod`)
- Go: `1.26.2`
- Build: `CGO_ENABLED=0`, `-trimpath`, `-buildvcs=false`
- SHA-256: `E40F6DA67D1D78B000685E0C3ED24A9230462302D7B2E5E96121DF2313534338`
- Authenticode: unsigned

The checksum is also stored in [`mcp-bridge.exe.sha256`](mcp-bridge.exe.sha256).

## Usage

```text
mcp-bridge [options]

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
