# ACP Schema

## Source

The `schema.json` file is the official JSON Schema for the
[Agent Client Protocol (ACP)](https://agentclientprotocol.com/).

It is downloaded from the upstream GitHub repository:

- **Repo:** <https://github.com/agentclientprotocol/agent-client-protocol>
- **Release asset:** `schema.json` from a `schema-v1.x` release, e.g.
  <https://github.com/agentclientprotocol/agent-client-protocol/releases/download/schema-v1.26.0/schema.json>
  (identical to `schema/v1/schema.json` at that tag; the repo root no longer has `schema/schema.json`)
- **Docs:** <https://agentclientprotocol.com/protocol/schema>

This is the **stable v1** schema. `schema.unstable.json` (same release) adds unstable features, and
`schema/v2/` (`schema-v2.0.0-alpha.*`) is the alpha of the next protocol generation; neither is used here.

## Current version

The file matches upstream release **schema-v1.26.0** (Oct 18, 2026).

## Purpose

This schema is the input for the `tools/AcpProtocolSchemaGenerator` tool, which parses
the `$defs` and generates the C# model classes (`Generated/AcpTypes.g.cs`, git-ignored)
used by the `AcpProtocol` project.

## Updating

1. Download `schema.json` from the latest `schema-v1.*` release on the
   [releases page](https://github.com/agentclientprotocol/agent-client-protocol/releases).
2. Replace the file in this directory.
3. Re-run the code generator:

   ```
   dotnet run --project tools/AcpProtocolSchemaGenerator -- src/AcpProtocol/Schema/schema.json src/AcpProtocol/Generated
   ```

4. Review the generated diffs and rebuild.
