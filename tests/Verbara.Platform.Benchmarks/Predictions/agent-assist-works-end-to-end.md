# Measurement plan: agent-assist-works-end-to-end (frozen before any run)

The M-cost gate of OpenSpec change `agent-assist-works-end-to-end`, task C.4 (design D15; verbara-meta/ADR-0030
decision 2): `CrossPodStreamCostBench`, legs `LocalEvent`, `ReceivedEnvelope` and `BridgeEmission`, modes `a`, `b`
and the control `n`. This file is committed before any run of the benchmark, and it is not edited after. Bytes decide
and time informs. Every run goes under `verbara-meta/scripts/xr-heavy-lock.sh`.

The text between the two markers is a verbatim copy of design D15's M-cost plan. Its sha256 is
`aafc5c99699857eae21f4ca89fcc0038ec58d52ddae3166f0f57fe2f1c5eacbd`. Re-check it before a run:

    awk '/^<!-- frozen-plan:begin -->$/{f=1;next} /^<!-- frozen-plan:end -->$/{f=0} f' \
      tests/Verbara.Platform.Benchmarks/Predictions/agent-assist-works-end-to-end.md | sha256sum

<!-- frozen-plan:begin -->
**M-cost** (ADR-0030 decision 2; spec audit round 2, F4 and the missed P2). D12 adds two costs that are paid **per
event**, not per session, on the SSE path. The audit showed that the earlier exemption ("no benchmark-covered
predicate is edited") does not cover them.

- **`Merge`:** every open stream's subscription becomes `eventBus.Events.Merge(feed.Events)`, upstream of the
  unchanged `.Where` (`SseEndpoints.cs:115-132`). Every local event of every tenant passes through one `Merge` per
  open stream.
- **The receiver's decode:** every Api replica that holds at least one open stream decodes every `agentassist.*`
  envelope of every tenant once, then offers the decoded record to each of its streams.

`ConversationContentDeliveryBench` cannot see either: it rebuilds the predicate by hand
(`ConversationContentDeliveryBench.cs:87-104`) and never runs the Rx pipeline. So this change adds a benchmark of the
real pipeline and an interleaved ABAB of it, **unconditional**, as a C.4 gate.

- **Shipped code, not a copy.** The candidate moves the stream's subscription chain, unchanged, into two internal
  static methods of `SseEndpoints`:
  - `StreamSource(PlatformEventBus, CrossPodStreamFeed?)`: `Events.Merge(feed.Events)`, or `Events` alone when no
    feed is registered;
  - `SubscribeDeliverable(IObservable<PlatformEvent> source, …, ChannelWriter<PlatformEvent>)`: the `.Where` and
    the `TryWrite` of `:115-132`.

  `StreamEvents` calls both. C.4's `git diff` check confirms that the `.Where` body and the channel are byte-equal to
  main apart from the move. The receiver exposes its per-envelope step as an internal `TryAccept(RemotePushEvent)`.
- **Benchmark:** `tests/Verbara.Platform.Benchmarks/CrossPodStreamCostBench.cs`, BenchmarkDotNet with
  `[MemoryDiagnoser]`, `ShortRun`. The mode comes from `CROSSPOD_STREAM_MODE`, so A and B run the same binary in
  separate processes, interleaved A/B/A/B, the pattern of `ConversationContentDeliveryBench`. Fixed workload: 50 open
  streams in one tenant (one named agent, two monitors, 47 other agents), each subscribed through
  `SubscribeDeliverable`.
  - **`LocalEvent`**: one local `agentassist.transcript`, a `conversation.message` and a non-content
    `conversation.state_changed` published on the bus per op. Mode `a` subscribes through `StreamSource(bus, null)`,
    main's source. Mode `b` subscribes through `StreamSource(bus, feed)`, the candidate's.
  - **`ReceivedEnvelope`**: one envelope of each of the four `agentassist.*` types per op, built as
    `RedisEventRelay.cs:179-191` builds it from `PlatformPushJsonContext` serialization of the fixture's example
    values, with a 120-character `text`. Mode `a` gives the receiver envelopes of a non-`agentassist` type, which it
    drops at the type check (today's cost: no decode). Mode `b` gives it the four `agentassist.*` envelopes, which it
    decodes and emits on the feed to the 50 streams. Run at 50 streams and, as information, at 1 stream.
  - **Control `n`**: as `b`, and the receiver also copies the payload into a new 2 KiB buffer per envelope, and each
    stream's source adds `.Select` to a new 64-byte object per event. The run must show both added amounts, or the
    benchmark cannot see the bytes it is meant to judge, and the ABAB is void.
- **A and B:** both legs on the candidate binary. A is main's source and no decode. B is the candidate's merged
  source and the decode. The ADR's "previous tag vs candidate" is realised this way because the previous tag has
  neither `StreamSource` nor a receiver, so any benchmark built against it would be a hand copy, which is the gap F4
  names. The `.Where` and the channel are the same code in both legs (C.4's diff check).
- **Predictions (frozen here):**
  - `LocalEvent`: B − A = 0 B per op. Rx `Merge` forwards each `OnNext` through its existing sink and gate and
    allocates nothing per notification. Median time B − A is under 50 ns per event and stream.
  - `ReceivedEnvelope` at 50 streams: B allocates between 0.5 and 4 KiB per envelope (the decoded record and its
    strings), and B at 50 streams minus B at 1 stream is under 512 B per envelope (decoded once, not per stream).
    Median under 20 µs per envelope.
- **Flip rules:**
  - `LocalEvent`, both B runs allocating more than A's larger run: the per-stream `Merge` is replaced by one
    process-wide merged source (`StreamSource` built once as a singleton `Events.Merge(feed.Events)` shared by every
    stream), then re-measured before the PR.
  - `ReceivedEnvelope`, both B runs over 8 KiB per envelope, or growing with the stream count by over 1 KiB per
    envelope: the receiver decodes only envelopes whose `Metadata.TenantId` has an open stream on this replica (a
    tenant set kept by the feed), then re-measured before the PR.
  - Time over 100 ns per event and stream (`LocalEvent`) or over 100 µs per envelope (`ReceivedEnvelope`) is recorded
    and raised to the operator, and does not stop the change on its own.
- **Run and evidence:** under `verbara-meta/scripts/xr-heavy-lock.sh`, .NET SDK, runtime, ISA and BenchmarkDotNet
  version recorded. The plan above is copied verbatim into
  `tests/Verbara.Platform.Benchmarks/Predictions/agent-assist-works-end-to-end.md` between the frozen-plan markers,
  with its sha256, and committed before any run. The four legs and the control go to
  `openspec/changes/agent-assist-works-end-to-end/evidence/sse-cost/` (local-only, ADR-0024), the per-run figures to
  task C.4, and the card to the Platform catalog (task 0.5).

The predicate benchmark stays conditional. If implementation edits `ConversationContentDelivery.IsDeliverable`,
`PlatformDeliveryFilter` or the `.Where` body, an ABAB of `ConversationContentDeliveryBench` (previous tag vs
candidate, bytes decide) becomes a further C.4 gate.

The uncached toggle read of D5 is a per-session cost, not on a benchmark-covered path; `C-AAE-TOGGLE` measures the
read, and `C-AAE-LOAD` measures the leader at target concurrency. D14's bind check and held identity lock are **per
event, not per session** (spec audit round 8, F2, P2: this paragraph used to list them as per-session), so the bridge's
emission path gets its own interleaved ABAB, a third leg of `CrossPodStreamCostBench`, frozen here:

- **`BridgeEmission`:** one of each of the four `agentassist.*` events of one session per op, with the fixture's example
  values and a 120-character `text`, over a real `PlatformEventBus` with one recording local subscriber and a no-op
  `IPushEventBus`. Mode `a` publishes the same four records straight through `PlatformEventBus.Publish` (no bridge, no
  identity: the floor). Mode `b` drives the candidate bridge's bound emission path (`AgentAssistSessionIdentity`
  already bound, the bind check, the identity lock held across `Publish`, the record built with the bound identity).
  As information, mode `b` also runs a flush of 128 held events once bound, reported per event. Main's bridge
  (`PublishWithPolicyAsync`) is not a leg: it cannot run on the candidate binary without a hand copy (the gap F4
  names), so B − A is the whole cost of the candidate path over the floor and an upper bound on its change from main.
- **Control `n`:** as `b`, plus a new 64-byte object allocated per emission inside the lock. The run must show the
  +64 B per event, or the ABAB is void.
- **Predictions:** B − A ≤ 64 B per event (the lock is a `System.Threading.Lock`, which allocates nothing; the bound
  bind check reads fields; both legs build the record once); median time B − A under 2 µs per event; the flush's per
  event bytes within 64 B of B's.
- **Flip rules:** both B runs over A's larger run by more than 256 B per event → the allocating closure or record copy
  is found and removed, then re-measured before the PR. Time over 10 µs per event is recorded and raised to the
  operator, and does not stop the change on its own.
- Run, evidence and frozen-plan copy as for the two legs above (task C.4). The receiver's decode
is per event, and the ABAB above prices one decode. `C-AAE-LOAD`, now run on two replicas, measures what it costs a
follower at the target rate.
<!-- frozen-plan:end -->
