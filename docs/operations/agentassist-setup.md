# Agent Assist setup

**Audience:** operators who turn Agent Assist on for a Platform deployment, rotate its speech provider credentials,
or run it with more than one Api replica.

Agent Assist listens to an answered queue call while it is in progress, transcribes both parties and sends the agent
live transcript lines and the caller's sentiment on `GET /api/v1/events/stream`. Supervisors see the assisted calls
in `GET /api/v1/supervisor/sessions/active` (the console's Monitor list). It needs the Agent Assist licence module.

## How a call is assisted

1. A queue call is answered. The Api replica that holds the voice AMI connection (the voice leader) decides whether
   to assist it: Agent Assist must be switched on (see **Switching it on**), the call must have a linked id, and the
   per-call claim must be free (see **More than one Api replica**).
2. That replica creates a snoop on each party's channel through ARI, in its own node Stasis app, and sets two
   channel variables on each snoop: `AUDIOSOCKET_UUID` (the snoop's own id) and `AUDIOSOCKET_HOST` (this replica's
   audio address, `host:port`).
3. Asterisk runs the snoop through the `[agentassist]` context, which connects it to that address with AudioSocket.
4. The replica streams the audio to the speech provider and publishes the results for the agent the call is
   connected to, and for the users of the call's tenant who read every conversation.

## Settings

| Setting (environment form) | Default | What it does |
|---|---|---|
| `AgentAssist__AudioSocket__AdvertisedHost` | none | The host Asterisk connects to for this replica's snoops. `auto`: the replica's own address on its route to the ARI host, resolved when the first call needs it. A literal host name or IP is used as given. It must name this replica only, never a service name that resolves to several replicas. |
| `AgentAssist__AudioSocket__Port` | `9093` | The port this replica listens on for snoop audio, and the port in its advertised address. |
| `AgentAssist__Deepgram__Language` | none | The language calls are transcribed in. Required: without it a session transcribes nothing. |
| `AgentAssist__Deepgram__Model` | the recognizer's default | The Deepgram model. |
| `AgentAssist__Enabled`, `AgentAssist__Provider`, `AgentAssist__Deepgram__ApiKey`, `AgentAssist__Deepgram__Endpoint` | off | Seed the stored on/off state, provider and credentials until an administrator first writes them through the route below. |

The shipped compose files set `AgentAssist__AudioSocket__AdvertisedHost: auto`. The audio stays inside the compose
network, so no port is published for it.

The Helm chart (`infra/k8s/helm/platform`) sets the address from `api.agentAssist.advertisedHost`, default
`$(POD_IP)` (the pod IP, from `status.podIP`), and declares the port as `containerPort` `aa-audio` from
`api.agentAssist.audioSocketPort` (default `9093`). No Service fronts the port: Asterisk connects to the pod that
created the snoop. The `r55-platform` default-deny policy drops that connection unless the policy
`allow-agentassist-audio` of `infra/k8s/manifests/network-policies.yaml` is applied: it admits TCP 9093 to
`platform-api` from the `r55-asterisk` namespace. If you change the port, change it in that policy too. The chart
supports an Asterisk that runs in the cluster's `r55-asterisk` namespace only: the policy admits no other source, and
`api.agentAssist.advertisedHost` is one value for every replica, so a literal address in it names the right replica
only when there is one.

The Api never fails to start over the address. When it is not set, or `auto` finds no route, each call is skipped
(no snoop is created), counted as `agentassist_sessions_skipped_total{reason="no_advertised_address"}`, and one
Warning names the missing setting.

## The Asterisk dialplan

An Asterisk used with Agent Assist must carry the `[agentassist]` context of `docker/asterisk-config/extensions.conf`
verbatim:

```text
[agentassist]
exten => audiosocket,1,AudioSocket(${AUDIOSOCKET_UUID},${AUDIOSOCKET_HOST})
 same => n,Hangup()
```

`AUDIOSOCKET_HOST` already holds `host:port`. A port written in the dialplan makes the address unreachable, and no
call is transcribed.

The Helm Asterisk chart (`infra/k8s/helm/asterisk`) is a load-test PBX: its dialplan answers, waits and hangs up, and
routes no call to a queue or an agent. It carries no `[agentassist]` context, and this release does not add one, so
no call on that chart is ever assisted. What the platform chart ships for Kubernetes is the Api side (the advertised
address, the container port and the NetworkPolicy above), for an Asterisk you provide that runs Platform's voice
dialplan, inside the pod network or outside it.

## Switching it on

Agent Assist has one state for the whole deployment: on or off, the speech provider and its credentials. Only the
Platform tenant changes it: an admin of the Platform tenant, a Platform-tenant user holding
`features:agent-assist:manage`, or a management API key scoped to that tenant. Any other tenant's caller gets 403.

| Verb | Path | Body | Answer |
|---|---|---|---|
| `GET` | `/api/v1/admin/features/agent-assist` | none | 200 `{ enabled, provider, credentialsConfigured, apiKeyMasked, endpointMasked, updatedAt, updatedBy }` |
| `PUT` | `/api/v1/admin/features/agent-assist` | `{ enabled, provider, credentials: { apiKey, endpoint } }` | 200 with the same body as `GET` |

- `enabled` true accepts `provider` `deepgram` only, with `credentials.apiKey`. Deepgram streams text while the call
  is in progress. `whisper`, `azure-whisper` and `google` answer 400: their recognizer returns text only after the
  audio ends, so it cannot transcribe a call that is still in progress.
- `credentials.endpoint` is optional and, when present, must be an absolute `ws://` or `wss://` URI.
- Credentials are encrypted with the deployment's DataProtection keyring before they are stored and are never
  returned (`"***"` when set). Back up the `data_protection_keys` table with the database: without it the stored
  credentials cannot be decrypted.
- With a `Postgres` connection string the state is stored in the database and shared by every replica, and a change
  applies to the next call on any replica, with no restart. Without a database it is held in memory on the replica
  that received the `PUT`.

Deepgram transcription in this release is verified against a protocol fake only, not against Deepgram's live
service.

## More than one Api replica

**The leader runs the sessions.** Only the replica that holds the voice AMI connection (the voice leader) creates
snoops and runs sessions; the others start none for a call and count it as `agentassist_sessions_gated_total`. Each
session also takes a per-call claim in the Platform database before it creates a snoop, so a call has at most one
session even while leadership moves between replicas. A call another replica already claimed is skipped as
`agentassist_sessions_skipped_total{reason="claim_held"}`; a claim that cannot be read or written (the database is
unreachable, or the call has no linked id) skips the call as `reason="claim_unavailable"`, so a call is never assisted
twice. A deployment without the Platform database takes no claim and keeps the on/off state in memory: it is
supported on one Api replica only.

**Every replica needs the Redis backplane.** An agent's event stream and a supervisor's Monitor list may be served by
a replica that is not running the session. With more than one Api replica, every replica needs
`ConnectionStrings__Redis`, set to the same Redis as Realtime: the session's events then reach the agent from any
replica, and the Monitor list is kept in Redis, so every replica lists the same sessions. The Helm chart and
`docker/docker-compose.scale.yml` set it. Without it each replica serves the four `agentassist.*` events and the
Monitor list from its own process, so Agent Assist is supported on one Api replica only.

## Metrics

On the Api's `/metrics`:

- `agentassist_sessions_skipped_total{reason}`: calls the engine did not assist, by reason, among them
  `no_advertised_address`, `claim_held` and `claim_unavailable`.
- `agentassist_sessions_gated_total`: calls not assisted on this replica because it is not the voice leader.
- `platform_agentassist_call_claim_total{outcome}`: per-call claims and releases (`claimed`, `held`, `error`,
  `released`, `release_error`).
- `platform_agentassist_recognizer_unavailable_total{reason}`: sessions that transcribe nothing (`disabled`,
  `provider_not_live`, `missing_value`, for example no `AgentAssist__Deepgram__Language`).
- `platform_agentassist_toggle_stale_total{path}`: on/off reads answered with the last known state because the
  database failed.
- `platform_agentassist_crosspod_received_total{outcome}`: events received from the replica running the session
  (`delivered`, `empty_payload`, `tenant_mismatch`, `decode_error`).
- `platform_agentassist_events_held_dropped_total{reason}`: events of a session never published because its call's
  tenant was never resolved, conflicted, or overflowed the hold.

## Verification

1. `GET /api/v1/admin/features/agent-assist` as a Platform-tenant admin answers 200 with `enabled` false on a fresh
   install (unless the `AgentAssist` section seeds it).
2. `PUT` it with `enabled` true, `provider` `deepgram` and `credentials.apiKey`; `GET` then shows `enabled` true,
   `credentialsConfigured` true and no key.
3. Answer a queue call. The agent's console shows transcript lines, and the call appears in the Monitor list.
4. If nothing arrives, read `/metrics` on the voice leader: a `no_advertised_address` skip means the address is
   missing or unresolvable; a `missing_value` recognizer count usually means `AgentAssist__Deepgram__Language` is
   not set; no skip and no session usually means Asterisk cannot reach the audio port (on Kubernetes, the
   NetworkPolicy above).
