-- =============================================================================
-- Verbara.Platform — Agent Assist shared state (031)
-- =============================================================================
-- agent-assist-works-end-to-end (design D4, D6).
--
-- agent_assist_feature_state: the deployment's one Agent Assist feature state,
-- shared by every replica, so an administrator's change made through any replica
-- is the state every replica reads. Exactly one row: the key `id` is fixed to 1
-- by its default and a CHECK, so the seed at first read is an
-- `INSERT ... ON CONFLICT (id) DO NOTHING` that two replicas can run at once.
--   * enabled               — the master switch;
--   * provider              — the lowercase speech provider id, NULL when none;
--   * protected_credentials — the provider credentials, encrypted with the Data
--                             Protection purpose AgentAssist.Credentials before
--                             they are written; never plain text;
--   * updated_at / updated_by — the last write; updated_by is 'appsettings' while
--                             the row still holds the configuration seed, so a
--                             changed configuration applies at restart only until
--                             an administrator writes the row.
-- Empty at rest: the row is written at first read, not here.
--
-- agent_assist_call_claims: one row per call that has a live Agent Assist
-- session, so that at most one replica runs a session for a call.
--   * call_key   — ServerId || '/' || LinkedId of the call;
--   * claim_id   — the id of the setup holding the claim; a release deletes the
--                  row only while it still carries that id;
--   * owner      — the instance that holds the claim;
--   * generation — the holding setup's generation for the call, so a later setup
--                  of the same owner replaces an earlier one in either order;
--   * claimed_at — when the claim was taken; a row older than the abandon age
--                  (24 hours) may be taken over by another owner.
-- Empty at rest; the primary key is the only index the claim and release need.
--
-- Additive, no backfill. Idempotent: CREATE TABLE IF NOT EXISTS, so a second run
-- changes nothing. Locks: two new, empty tables; no existing table is touched.
-- Rollback: an older binary never names either table, so both can stay unused.
-- To remove them,
--   DROP TABLE IF EXISTS agent_assist_call_claims;
--   DROP TABLE IF EXISTS agent_assist_feature_state;
-- (dropping the state table discards an administrator's saved settings; the
-- next start of this version seeds it again from configuration).
-- =============================================================================

CREATE TABLE IF NOT EXISTS agent_assist_feature_state (
    id                    SMALLINT    PRIMARY KEY DEFAULT 1 CONSTRAINT ck_agent_assist_feature_state_one_row CHECK (id = 1),
    enabled               BOOLEAN     NOT NULL,
    provider              TEXT        NULL,
    protected_credentials JSONB       NULL,
    updated_at            TIMESTAMPTZ NOT NULL,
    updated_by            TEXT        NOT NULL
);

CREATE TABLE IF NOT EXISTS agent_assist_call_claims (
    call_key   TEXT        PRIMARY KEY,
    claim_id   UUID        NOT NULL,
    owner      TEXT        NOT NULL,
    generation BIGINT      NOT NULL,
    claimed_at TIMESTAMPTZ NOT NULL
);
