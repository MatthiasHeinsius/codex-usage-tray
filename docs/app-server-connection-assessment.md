# Codex app-server connection lifecycle

Checked 2026-09-23 against the [official Codex App Server documentation](https://learn.chatgpt.com/docs/app-server).

## Documented behavior

- `codex app-server` uses newline-delimited JSON over stdio by default. Its JSON-RPC responses echo a request ID; notifications have no ID. The [official example](https://learn.chatgpt.com/docs/app-server#message-schema) starts one child process, reads its stdout continuously, and writes several messages to its stdin.
- A client must send `initialize` and then `initialized` **once per transport connection**. Requests before initialization fail, and a second `initialize` on the same connection fails. This permits repeated account requests on one initialized connection without repeating the handshake. See [initialization and lifecycle](https://learn.chatgpt.com/docs/app-server#initialization).
- `account/rateLimits/read` fetches ChatGPT rate limits, and `account/usage/read` fetches account token activity. The server can also emit `account/rateLimits/updated` when limits change and `account/updated` when authentication mode changes. See [auth endpoints](https://learn.chatgpt.com/docs/app-server#auth-endpoints) and [rate-limit examples](https://learn.chatgpt.com/docs/app-server#6-rate-limits-chatgpt).
- Codex-managed ChatGPT authentication persists tokens and refreshes them automatically. A long-lived client should still handle auth changes reported through `account/updated`. See [authentication modes](https://learn.chatgpt.com/docs/app-server#authentication-modes).
- The documentation describes the protocol and initialization but gives **no measured startup cost, idle memory or CPU figure, recommended process lifetime, notification delivery guarantee for an otherwise idle rate-limit client, or specific stdio shutdown/restart recipe**. The [Codex SDK example](https://learn.chatgpt.com/docs/codex-sdk) scopes a server-backed client with `with Codex()`, but it does not establish a lifetime rule for a tray app.

## Inference for this tray app

- Keeping the stdio process open is protocol-compatible. It saves repeated process creation and initialization. Each scheduled `account/rateLimits/read` remains an account request, so connection reuse alone does not reduce the polling count or prove a network saving.
- Keep a dedicated stdout reader for the entire connection lifetime. Match replies by ID and accept notifications between replies. Handle process exit, broken pipes, timeouts, and auth changes by failing pending requests and establishing a newly initialized process. These are client design consequences of the documented message format, not a published OpenAI reliability recipe.
- Do not replace the current poll with `account/rateLimits/updated` alone until a signed-in idle-session test establishes when that notification is emitted. The documentation says it is emitted when limits change, but does not state how the server learns about changes while otherwise idle.
- Decide on persistence from an end-to-end comparison of process CPU time, request latency, idle private memory, and failures over realistic tray lifetimes. The official documentation makes no performance recommendation for this workload.

## Signed-in measurements

Measured on Windows on 2026-09-23 with `codex-cli 0.156.1`. The probe launched
`codex.exe app-server --stdio` directly, sent the same experimental-capability
handshake and `account/rateLimits/read` request as the tray app, and discarded
the account response. The tray app currently starts Codex through `cmd.exe`, so
these figures exclude that wrapper. Six pairs alternated request order between
a new server and one reused server, with ten seconds between pairs. These are
short-run samples, not a benchmark at the app's one-minute production cadence.

| Pair | New server init | New server read | New server total | Reused server read | Total saved by reuse |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 127 ms | 598 ms | 725 ms | 491 ms | 234 ms |
| 2 | 106 ms | 592 ms | 698 ms | 793 ms | -95 ms |
| 3 | 100 ms | 609 ms | 709 ms | 624 ms | 85 ms |
| 4 | 104 ms | 591 ms | 695 ms | 527 ms | 168 ms |
| 5 | 106 ms | 530 ms | 636 ms | 539 ms | 97 ms |
| 6 | 118 ms | 536 ms | 654 ms | 452 ms | 202 ms |

The median paired saving was **133 ms**. Five pairs favored reuse; one did not.
After a further 60 seconds idle, a read on the reused server took 678 ms. The
read-time variation is large enough that a stronger latency claim needs more
requests at the normal one-minute interval.

The reused process generally held **25–28 MB private committed memory** and
about **95–97 MB working set** after reads. One transient sample reached 46 MB
private, then fell back to 26 MB. After two minutes idle, it held 25.3 MB
private and 94.9 MB working set. Working set includes shared pages and is not
all incremental physical memory.

A separate CPU probe recorded 234–266 ms total process CPU for two fresh
initialize-and-read sessions. Reused reads consumed 0–47 ms in that small
sample, but startup work continued after the first response: in a two-minute
idle probe, process CPU increased 375 ms in the first 30 seconds, then 0,
15.6, and 0 ms in the next three 30-second intervals. This suggests deferred
startup work, rather than a steady idle CPU cost. Request CPU attribution from
these brief samples is uncertain; a longer run must count all process CPU time.

## Decision for the current app

For a background poll once per minute, the observed latency saving is modest
beside a roughly 0.5–0.8 second account read. Reuse does not reduce the number
of account requests and retains about 25 MB of private memory. Keep the current
short-lived connection unless a normal-cadence, longer A/B run shows a material
CPU or user-visible latency benefit. Persistence becomes more attractive if the
app makes more frequent requests or uses server notifications that require a
live connection.

A decisive A/B run would alternate fresh and reused variants at the same
one-minute polling cadence over at least a day. Record refresh latency (median
and 95th percentile), total child-process CPU time including post-response work,
private memory, account request count, and timeout/restart failures. Apply the
same authentication state and CLI version to both variants. Do not substitute
the short-run probe for this reliability and long-run resource check.
