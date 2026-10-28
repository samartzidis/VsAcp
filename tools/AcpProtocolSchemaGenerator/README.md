# AcpProtocolSchemaGenerator

A code generation tool that transforms the official [Agent Client Protocol (ACP)](https://agentclientprotocol.com/) JSON Schema into C# source code. It produces `AcpTypes.g.cs` — the complete set of records, enums, and JSON converters consumed by the `AcpProtocol` library.

## Usage

```bash
dotnet run --project tools/AcpProtocolSchemaGenerator -- <schema.json> <output-dir>
```

**Example** (run from repo root):

```bash
dotnet run --project tools/AcpProtocolSchemaGenerator -- src/AcpProtocol/Schema/schema.json src/AcpProtocol/Generated
```

| Argument | Description |
|---|---|
| `<schema.json>` | Path to the ACP JSON Schema file |
| `<output-dir>` | Directory where `AcpTypes.g.cs` will be written |

Exit code `0` on success, `1` on error.

## What it generates

A single file, `AcpTypes.g.cs`, containing everything derived from the schema:

- **Records** — one per ACP object type, with required/optional properties and `[JsonExtensionData]` for forward compatibility
- **Enums** — string-based enums decorated with `[JsonStringEnumMemberName]` for exact wire-format fidelity
- **Merged records** — objects with shared properties plus untagged `anyOf`/`oneOf` variants (e.g. `CreateElicitationRequest`, `SetSessionConfigOptionRequest`) become one record with all variants' properties; properties not present in every variant are optional
- **Tagless unions** — `anyOf` of `$ref`s without a `discriminator`. When branches carry a `const` tag (e.g. `McpServer`'s `type: "http"`, `ElicitationPropertySchema.type`), the tag goes on the variant record as a get-only property, so it is written however the record is serialized, and the converter switches on it: a known tag reads as its variant, no tag as the one untagged branch (`McpServerStdio`, `AuthMethodAgent`) or an error if there is none, and any other tag as `<Union>Unknown`, which keeps the tag in `RawType` and the other fields in `ExtensionData`. The generator fails on tags it can't map exactly: two tag properties, more than one untagged branch, a tagged variant used outside its union, or a variant that declares the tag itself. Unions with no tags probe for a property unique to each variant
- **Discriminated union types** — abstract base records plus a concrete variant per known discriminator value, with an `Unknown` fallback for forward compatibility
- **Custom JSON converters** — one per union type, handling polymorphic deserialization
- **`AcpJsonSerializerContext`** — a source-generated `JsonSerializerContext` covering all types, enabling reflection-free AOT-compatible serialization

Do not edit `AcpTypes.g.cs` by hand — re-run the generator instead.

## Updating the schema

1. Download the latest `schema.json` from the [agentclientprotocol/agent-client-protocol](https://github.com/agentclientprotocol/agent-client-protocol) releases
2. Replace `src/AcpProtocol/Schema/schema.json`
3. Re-run the generator (command above)
4. Review the generated diff and rebuild the solution

The schema file checked into the repo is at `src/AcpProtocol/Schema/schema.json`.

## How it works

`CSharpGenerator` reads the `$defs` section of the schema and classifies each definition into one of five kinds:

| Kind | Generated as |
|---|---|
| Object with properties | `public record T { ... }` |
| Fixed set of string constants | `public enum T { ... }` |
| `oneOf`/`anyOf` with a discriminator property | Abstract base record + variant records + converter |
| `oneOf`/`anyOf` without a discriminator | Abstract base record + property-probing converter |
| Thin wrapper around a primitive | Inlined as the underlying type (no separate record) |

A small set of internal JSON-RPC envelope types (`AgentRequest`, `AgentResponse`, `Error`, `RequestId`, etc.) are excluded from generation — they are defined manually in the `AcpProtocol` library.

## Dependencies

Targets **.NET 9** (build-time only). The generated code itself has no dependencies beyond `System.Text.Json`, which is already required by `AcpProtocol`.
