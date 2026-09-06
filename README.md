# MCPulse for .NET

Analytics for MCP servers, for the [official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk).

```csharp
using MCPulse;

McPulse.Configure(new McPulseOptions { Key = "mp_live_…" });
```

Then wrap each tool invocation:

```csharp
return await McPulse.RecordAsync(
    toolName: request.Params.Name,
    arguments: request.Params.Arguments,
    clientName: server.ClientInfo?.Name,
    invoke: () => next(request, cancellationToken));
```

Best placed in whatever your host already uses to see every call — a
`CallTool` handler wrapper, or the filter your server builder exposes.

## Install

```bash
dotnet add package MCPulse
```

Zero package references. This library is loaded into other people's servers,
and a transitive dependency that conflicts with what the customer already pins
is a support burden with no upside. The canonicaliser, the HTTP call and the
buffering are all in the base class library.

## Options

| Property | Default | Meaning |
|---|---|---|
| `Key` | — | Ingest key, `mp_live_…`, minted per MCP in the dashboard |
| `Endpoint` | `https://api.getmcpulse.com` | Point at a local API while developing |
| `Enabled` | `true` | `false` makes everything a no-op — useful in tests and CI |
| `Debug` | `false` | Log what is sent, and why a send failed, to **stderr** |

An empty key turns it off, so a server started without its key configured is
silent rather than a source of 401s on every flush.

## One known gap

`RecordAsync` wraps your handler, so a throw arrives as a throw rather than as
the `isError` result the server would have converted it into — that is what
separates `crashed` from `tool_error`.

What it cannot see is `bad_args`. The .NET SDK binds and validates arguments
before the handler runs, so a rejected argument set never reaches the wrapper.
Reporting it anyway would mean reading the difference back out of an error
message, and error strings are not an interface anyone promised to keep.

## What leaves your process

Sizes and hashes. Arguments and results do not, and no option turns that on.

## The three rules

1. **Never throw.** Every entry point swallows. Your exception is re-raised
   untouched; ours never reach you.
2. **Never block.** A bounded `Channel` with `DropOldest` means `TryWrite`
   never blocks and never fails — the same mechanism gives rule 2 and the
   memory ceiling at once.
3. **Never store customer data.** See above.

## Cross-language consistency

`ArgsHash` is the first 12 hex characters of the SHA-256 of the
[RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) canonical form of the
arguments. `tests/MCPulse.Tests/canonical.json` is the shared conformance suite
every MCPulse SDK runs.

.NET needed two things undone and got one for free. `System.Text.Json` escapes
`<`, `>` and `&` by default, and .NET's round-trip number format writes `1E+21`
and `1E-07` where ECMAScript writes `1e+21` and `1e-7`. The free part: RFC 8785
sorts keys by UTF-16 code unit, which is exactly `StringComparer.Ordinal`.

## Running the tests

```bash
dotnet run --project tests/MCPulse.Tests
```

## Licence

MIT
