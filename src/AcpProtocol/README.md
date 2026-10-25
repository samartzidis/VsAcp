# AcpProtocol

A .NET library implementing the [Agent Client Protocol (ACP)](https://agentclientprotocol.com/) — a JSON-RPC 2.0 based protocol for bidirectional communication between an IDE client and an AI agent process.

## Overview

AcpProtocol provides the transport and message layer for a two-way RPC system over line-delimited stdin/stdout streams. It handles serialization, request/response correlation, and error propagation, leaving the application logic to implementors of two interfaces: `IAcpAgent` and `IAcpClient`.

```
  IDE (client side)                 Agent process (server side)
  ─────────────────                 ──────────────────────────
  ClientSideConnection ──stdin/stdout──> IAcpAgent implementation
       │                                        │
       └── IAcpClient implementation <──────────┘
             (file I/O, terminals,
              permissions, session updates)
```

## Core Concepts

### Roles

| Interface | Who implements it | Called by |
|---|---|---|
| `IAcpAgent` | The AI agent process | The IDE (client) |
| `IAcpClient` | The IDE (client) | The AI agent process |

### `IAcpAgent` — agent-side operations

Methods the client calls on the agent:

- `InitializeAsync` — protocol handshake and capability negotiation
- `AuthenticateAsync` — authentication flow
- `NewSessionAsync` / `LoadSessionAsync` / `DeleteSessionAsync` — session lifecycle
- `PromptAsync` — send a user prompt, stream back results
- `CancelAsync` — cancel an in-progress operation
- `SetSessionModeAsync` / `SetSessionConfigOptionAsync` — runtime configuration
- `ExtMethodAsync` / `ExtNotificationAsync` — extension points for custom methods

### `IAcpClient` — client-side callbacks

Methods the agent calls back into the IDE:

| Category | Methods |
|---|---|
| File system | `ReadTextFileAsync`, `WriteTextFileAsync` |
| Permissions | `RequestPermissionAsync` |
| Elicitation | `CreateElicitationAsync`, `CompleteElicitationAsync` |
| Session | `SessionNotificationAsync` |
| Terminal | `CreateTerminalAsync`, `TerminalOutputAsync`, `KillTerminalCommandAsync`, `ReleaseTerminalAsync`, `WaitForTerminalExitAsync` |
| Extensions | `ExtMethodAsync`, `ExtNotificationAsync` |

## Transport

- **Format:** JSON-RPC 2.0, one message per line (newline-delimited)
- **Stream:** `TextReader` / `TextWriter` (typically `stdin` / `stdout`)
- **Message types:** request (with response), notification (fire-and-forget)

### Method names

Agent methods are mostly `session/*` (plus bare `initialize` and `authenticate`); client callbacks use `fs/*`, `terminal/*`, `elicitation/*`, and `session/*` namespaces.

## Usage

Instantiate `ClientSideConnection` on the IDE side, passing a factory that creates your `IAcpClient` implementation:

```csharp
var connection = new ClientSideConnection(
    agentProxy => new MyAcpClient(agentProxy),
    stdin,
    stdout,
    logger
);

connection.Open(); // start the read loop

var init = await connection.InitializeAsync(new InitializeRequest { ... });
var session = await connection.NewSessionAsync(new NewSessionRequest { ... });
var response = await connection.PromptAsync(new PromptRequest { ... });
```

The agent receives calls via your `IAcpAgent` implementation and can reach back to the client through the `IAcpClient` proxy that `ClientSideConnection` wires up automatically.

## Error handling

Throw `AcpException` inside any handler to return a structured JSON-RPC error response to the caller. Standard `JsonRpcErrorCode` values (`InvalidRequest`, `MethodNotFound`, `InternalError`, etc.) are defined alongside any ACP-specific codes.

## Generated types

`AcpTypes.g.cs` is auto-generated from the [ACP schema](Schema/schema.json) (currently schema-v1.26.0). Do not edit it by hand — regenerate it using the `AcpProtocolSchemaGenerator` tool in `tools/AcpProtocolSchemaGenerator/` when the schema is updated.

## Dependencies

| Package | Purpose |
|---|---|
| `System.Text.Json` | JSON serialization (source-generated) |
| `Microsoft.Extensions.Logging.Abstractions` | Logging abstraction |
| `Microsoft.Bcl.AsyncInterfaces` | `IAsyncEnumerable` on .NET 4.7.2 |
| `PolySharp` | C# language polyfills |

Targets **.NET Standard 2.0** for compatibility with both the .NET Framework 4.7.2 Visual Studio extension and the schema generator tool.
