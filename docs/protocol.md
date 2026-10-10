# The protocol

This page describes how a host and an adapter talk, in enough detail to write an SDK in another
language, or an `exec` adapter that speaks the protocol directly. Adapter authors using the .NET,
Python or Node SDK do not need it.

The message definitions are in
[`SW.Serverless.Contract/Protos/adapter.proto`](../SW.Serverless.Contract/Protos/adapter.proto)
(package `sw.serverless.v1`). This page explains how they are used.

- [Overview](#overview)
- [Starting: the stdin handshake](#starting-the-stdin-handshake)
- [The stream](#the-stream)
- [Hello and Ready](#hello-and-ready)
- [Frames](#frames)
- [Encoding](#encoding)
- [Errors](#errors)
- [Stopping](#stopping)
- [Classic sessions on protocol 2](#classic-sessions-on-protocol-2)
- [Describe](#describe)
- [Protocol 1](#protocol-1)
- [Checklist for a new SDK](#checklist-for-a-new-sdk)

## Overview

```
host                                         adapter process
 |  start process (stdin, stdout, stderr piped)    |
 |  stdin: {"socket": ..., "token": ...}\n -------->|
 |                                                 |  connect to the socket
 |<------------- gRPC Attach stream opened --------|
 |<------------- Hello (token, commands, ...) -----|
 |------------- Ready (settings, max in flight) -->|
 |------------- Invoke #1 ------------------------>|
 |<------------ InvokeResult #1 -------------------|
 |------------- Ping #2 -------------------------->|
 |<------------ Pong #2 ---------------------------|
 |<------------ Event #1 (adapter's id) -----------|
 |------------- EventAck #1 ---------------------->|
 |------------- Shutdown ------------------------->|
 |                                                 |  finish, close the stream, exit
```

This is **protocol 2**. The host speaks versions 2 to 2 today. **Protocol 1**, the classic text
protocol, is used only by .NET adapters on `Runner.Run`; see [Protocol 1](#protocol-1).

## Starting: the stdin handshake

The host starts the adapter as a child process, with its working directory set to the folder of
the entry file:

| Runtime | Command |
|---|---|
| `dotnet` | `dotnet <entry>` |
| `python` | `python3 -u <entry>` (with `PYTHONUNBUFFERED=1`) |
| `node` | `node [--max-old-space-size=<MB>] <entry>` |
| `exec` | `<entry>` |

Immediately after starting it, the host writes **one line of JSON** to the adapter's standard
input:

```json
{"socket":"/tmp/swsl-4182.sock","pipe":null,"token":"8c0e0b0f6a7c4e5f9f4d2a2c7b1e9d33","adapterId":"acme.orders","instanceKey":"main","protocol":2}
```

| Field | Meaning |
|---|---|
| `socket` | The Unix domain socket to connect to (Linux, macOS). |
| `pipe` | The named pipe to connect to (Windows), as a pipe name. Exactly one of `socket` and `pipe` is set. |
| `token` | A one-time token. Send it back in Hello. |
| `adapterId` | The adapter id, possibly with a pinned version (`acme.orders/1.2.0`). Echo it in Hello. |
| `instanceKey` | Which instance this process is. Echo it in Hello. |
| `protocol` | The newest protocol version the host speaks. |

Then:

- **Keep reading standard input.** The host keeps it open while it lives. End of file means the host
  has gone: stop and exit, rather than running on as an orphan.
- **Standard output and standard error** are not a channel. The host keeps the last lines (200 by
  default) and shows them when the adapter fails to connect or exits unexpectedly. Write startup
  failures there.
- Nothing secret is on the command line: settings arrive later, over the socket.

## The stream

Connect to the socket or pipe and speak **HTTP/2 without TLS** (prior knowledge, no upgrade). The
host serves one gRPC method:

```proto
service AdapterHost {
    rpc Attach (stream AdapterFrame) returns (stream HostFrame);
}
```

Open one call to `POST /sw.serverless.v1.AdapterHost/Attach` with `content-type: application/grpc`
and `te: trailers`. Each message in either direction is the gRPC framing: one byte `0` (not
compressed; compression is not used), a 4-byte big-endian length, then the protobuf-encoded
message. A message may be up to 64 MB. The `:authority` is not checked; the Python and Node SDKs
use `localhost`.

The adapter has to connect, open the stream and send Hello within the host's handshake timeout (30
seconds by default), or the host kills it.

Everything travels on this one stream, in both directions, interleaved. The adapter must keep
reading frames while commands run: a command that takes a minute must not stop pings being
answered, or the host will restart the adapter. Writes to the stream must not interleave; use one
writer.

## Hello and Ready

The **first frame** the adapter sends must be `Hello`:

| Field | Meaning |
|---|---|
| `token` | From the handshake. An unknown or already used token ends the call with `PERMISSION_DENIED`. |
| `adapter_id`, `instance_key` | From the handshake. |
| `protocol_version` | The version the adapter will use: the lower of the handshake's `protocol` and the newest it speaks. Outside the host's range, the call ends with `FAILED_PRECONDITION`. |
| `sdk_version`, `sdk_language` | Your SDK's version and language (`dotnet`, `python`, `node`, `go`, ...). |
| `capabilities` | Strings: `command:<Name>` for every command; `resident` if it has a start hook; `resettable` if it handles Reset meaningfully; `cancel` if it understands the Cancel frame. |
| `commands` | A `CommandInfo` per command: `name`, `description`, `returns_value`, `input_schema` and `output_schema` (JSON Schema documents as strings, empty when none), and `parameter_type` / `parameter_schema` (a type name, and a .NET-style property map; may be left empty by other languages). |
| `settings` | A `SettingInfo` per declared setting: `name`, `description`, `required`, `secret`, `default_value` (empty for none), `type` (`text`, `multiline`, `number`, `boolean`, `select`, `json`). |
| `kinds` | The kinds it implements. |
| `contracts` | The contracts it implements: name to version. |

A first frame that is not Hello ends the call with `INVALID_ARGUMENT`.

The host answers with `Ready`:

| Field | Meaning |
|---|---|
| `max_in_flight` | How many events the adapter may have waiting for an EventAck at once. |
| `startup_values` | The settings, name to value. Includes `CorrelationId` for classic sessions. |
| `adapter_values` | Extra values from the package's storage metadata and the host. |

After Ready the host considers the instance ready and may send Invoke at once. An SDK should build
the adapter and run its start hook on Ready, before running commands, and must keep reading frames
meanwhile or queue the Invokes that arrive.

## Frames

Every frame has an `id` (int64) and a `traceparent` (W3C trace context, may be empty), and one body.

`HostFrame` bodies, host to adapter:

| Body | Reply | Meaning |
|---|---|---|
| `Ready` | none | See above. Sent once. |
| `Invoke` | `InvokeResult` with the same `id` | Run a command. |
| `Cancel` | none | The host has given up on the Invoke with this `id`. Sent only to adapters with the `cancel` capability. |
| `Ping` | `Pong` with the same `id` | Heartbeat. |
| `Reset` | `InvokeResult` with the same `id` | End a session. |
| `SetLogLevel` | none | `level`: the lowest log level to send, 0 (Trace) to 5 (Critical). |
| `Shutdown` | none (close the stream and exit) | `reason`, `drain`. |
| `EventAck` | none | The answer to the adapter's Event with this `id`. |
| `StateResult` | none | The answer to the adapter's StateRequest with this `id`. |

`AdapterFrame` bodies, adapter to host:

| Body | Reply | Meaning |
|---|---|---|
| `Hello` | `Ready` | First frame. |
| `InvokeResult` | none | The answer to an Invoke or a Reset, with its `id`. |
| `Pong` | none | The answer to a Ping, with its `id`. |
| `Event` | `EventAck` with the same `id` | Hand an event to the host. |
| `StateRequest` | `StateResult` with the same `id` | Get, set or delete a piece of state. |
| `LogEntry` | none | A log line. `id` 0. |
| `Metric` | none | A metric value. `id` 0. |

Ids for frames the host starts (Invoke, Ping, Reset) come from the host's counter. Ids for frames
the adapter starts (Event, StateRequest) come from the adapter's own counter. The two do not
collide because each side matches replies only against its own pending requests.

**Invoke**

| Field | Meaning |
|---|---|
| `command` | The command name. |
| `payload` | The argument, [encoded](#encoding). Empty for none. |
| `timeout_seconds` | How long the host will wait. The adapter may cancel its own work after it. |
| `session_id` | Groups several calls into one session (a pooled lease). Empty means the call stands alone. |
| `properties` | Per-call values, read like settings; they win over startup values of the same name. |

Several Invokes may be in flight at once. Answer each with `InvokeResult { payload }` or
`InvokeResult { error { type, message, detail } }`, in any order. If the host has timed out or
cancelled the call, it discards a late answer.

**Ping / Pong.** The host pings every heartbeat (15 seconds by default) and restarts the adapter
after three unanswered pings. Answer quickly, even while busy. `Pong` fields: `connected`, `state`
(free text), `last_message_unix_ms`, `in_flight`, `last_error`, `details` (string map).

**Reset.** Answer with an `InvokeResult` with an empty payload once per-session state for
`session_id` is gone, or with an error. The host waits up to 15 seconds before handing the
process to another session.

**Event / EventAck.** `Event` fields: `payload` (bytes), `dedupe_key`, `content_type`, `headers`
(string map), `endpoint`. The adapter waits for `EventAck { accepted, reference, error }`, and
acknowledges its own source only if `accepted`. It must not have more than `max_in_flight` events
unanswered at once. A `traceparent` on the Event frame is passed to the host's event sink.

**StateRequest / StateResult.** `StateRequest { op, name, value }` with `op` `GET` (0), `SET` (1)
or `DELETE` (2); `value` is used by SET only. `StateResult { found, value, error }`: `found` is false
for a GET of a name with nothing stored. An `error` means the host could not do it; report it to
the adapter's code rather than ignoring it.

**LogEntry.** `level` (0 Trace, 1 Debug, 2 Information, 3 Warning, 4 Error, 5 Critical), `message`,
`exception`, `properties` (string map), `timestamp_unix_ms`. Logs and metrics may be dropped under
load; never let them hold up results, pongs or events.

**Metric.** `name`, `value` (double), `tags` (string map). The host adds the value to a counter.

## Encoding

Payloads (Invoke arguments, InvokeResult results, Event payloads) are bytes. Every SDK uses the
same rule:

| Value | Bytes |
|---|---|
| A string | Its UTF-8 text, not a JSON string. |
| Bytes | As they are. |
| Nothing | Empty. |
| Anything else | JSON (UTF-8). |

The .NET host serializes objects with Newtonsoft.Json, keeping property names as declared, and
reads a result as raw text when the caller asks for a string, otherwise as JSON. An SDK should do
the same: decode the argument according to the command's declared input type, and encode the result
the same way.

## Errors

An `Error` has `type`, `message` and `detail`.

- `type` is the error's type as the adapter names it: an exception's full type name in .NET, a
  qualified class name in Python, a constructor name in Node, or a type the author chose. Hosts show
  it to callers as `AdapterInvocationException.AdapterExceptionType`.
- `message` is for people.
- `detail` is a stack trace or similar, for logs.

Conventions: an unknown command fails with type `MissingMethodException` (or
`System.MissingMethodException`), and a cancelled call with `OperationCanceledException`.

## Stopping

`Shutdown { reason, drain }` asks the adapter to stop. With `drain`, it should stop taking new work,
let commands already running finish and send their results, and exit, within 30 seconds; without,
within 5. Run the adapter's stop hook first, then wait for running commands, then send what is
still queued, close the stream and exit. The host kills the process after the deadline.

The host sends `Shutdown` with `drain` when it stops an instance by request (by default), when an
adapter crosses its soft memory or sustained CPU limit, and when the host itself stops.

## Classic sessions on protocol 2

A Python, Node or `exec` adapter, or a .NET adapter on protocol 2, is also run for classic sessions
through `IServerlessService`. Underneath, the host:

1. starts a process for the session with instance key `classic-<random>`;
2. sends Ready with the session's settings plus `CorrelationId`;
3. sends one Invoke at a time, waiting for each answer;
4. sends `Shutdown` with `drain` false when the session is disposed.

An adapter needs nothing special for this. `GetExpectedStartupValues()` answers from the `settings`
in Hello.

## Describe

Every SDK must handle the `--describe` command-line argument: print one JSON document to standard
output and exit with code 0, without reading the handshake or connecting anywhere. Tools run it
with standard input closed, and give up after 30 seconds.

```json
{
  "describeVersion": 1,
  "sdkLanguage": "python",
  "sdkVersion": "10.2.2",
  "lifecycle": "classic",
  "protocol": { "min": 2, "max": 2 },
  "settings": [
    { "name": "Greeting", "description": "What to say before the name.", "type": "text", "required": false, "secret": false, "default": "Hello" }
  ],
  "commands": [
    {
      "name": "Add",
      "description": "Adds two numbers.",
      "inputSchema": { "type": "object", "properties": { "A": { "type": "integer" }, "B": { "type": "integer" } }, "required": ["A", "B"] },
      "outputSchema": { "type": "integer" },
      "returnsValue": true
    }
  ],
  "kinds": [],
  "contracts": {},
  "warnings": []
}
```

| Field | Meaning |
|---|---|
| `describeVersion` | `1`. |
| `sdkLanguage`, `sdkVersion` | As in Hello. `sdkLanguage` must be set. |
| `lifecycle` | `classic` or `resident`: how the adapter's entry point runs it. |
| `protocol` | `{ "min": 2, "max": 2 }` for protocol 2. .NET classic adapters report `{ "min": 1, "max": 1 }`. |
| `settings` | `name`, `description`, `type`, `required`, `secret`, `default`. Leave out a secret's default. |
| `commands` | `name`, `description`, `inputSchema` (JSON Schema, or null for no argument), `outputSchema` (null for no result), `returnsValue`. |
| `kinds`, `contracts` | As in Hello. |
| `warnings` | Anything that made the description incomplete, such as an adapter that could not be built without its settings. |

Unknown fields are kept by readers, so an SDK may add its own. `sw-serverless build` writes the
manifest from this; the conformance kit compares it with the manifest.

## Protocol 1

Protocol 1 is the original .NET text protocol, used by `Runner.Run`. Hosts use it only for .NET
adapters whose manifest has no `protocol` of 2 or more; it is not available to other languages.
In brief:

- The host starts `dotnet <entry> --values-on-stdin` and writes three lines on standard input, each
  base64-encoded JSON: host options, startup values, adapter values. (Adapters on SDKs older than
  10.1.0 get the same three values as command-line arguments instead.)
- A call is one line, `#!#<Command>#!#<argument>#!#`, with newlines in the argument written as
  `{{newline}}` and a null argument as `{{null}}`. The answer is one line on standard output,
  `#!#<result>#!#`, or `{{error}}<exception text>`.
- `{{expected}}` as the command asks for the declared settings; `{{quit}}` ends the process.
- Log lines go to standard error, starting with `{{log.information}}`, `{{log.warning}}` or
  `{{log.error}}`.
- One call at a time. A timed-out call kills the process.

New SDKs should implement protocol 2.

## Checklist for a new SDK

1. On `--describe`, print the [description](#describe) and exit 0.
2. Otherwise read one line from standard input and parse the handshake. Refuse a `protocol` lower
   than 2.
3. Keep watching standard input; on end of file, stop.
4. Connect to `socket` (or `pipe`), open `Attach`, send Hello with the token and your commands,
   settings, kinds, contracts and capabilities.
5. On Ready, store the settings, build the adapter, run its start hook without blocking the frame
   reader.
6. Run each Invoke on its own task; answer with its id; apply `properties` and `session_id` to that
   call only; encode as above; report errors with a type.
7. Answer Ping with Pong promptly, from the adapter's status hook if it has one.
8. Answer Reset with an InvokeResult.
9. Send Events and StateRequests with your own ids, wait for the matching reply, and respect
   `max_in_flight`.
10. Send logs and metrics on a droppable queue, after results and pongs.
11. On Cancel, cancel the call's work (if you listed the `cancel` capability).
12. On Shutdown, run the stop hook, let running commands finish within the deadline, flush, close,
    exit.

The Python SDK (`sdk/python/src/sw_serverless`) implements all of this with the standard library
alone, including HTTP/2 and protobuf; it is a compact reference.
