-- Durable batch markers make a cancelled import safe to resume with the same
-- operation ID. The payload digest rejects a different preview on retry.
CREATE TABLE IF NOT EXISTS import_job (
  op_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  payload_sha TEXT NOT NULL,
  batch_count INTEGER NOT NULL CHECK (batch_count > 0),
  completed INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0,1)),
  result_id INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT, WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS import_batch (
  op_id TEXT NOT NULL REFERENCES import_job(op_id),
  batch_no INTEGER NOT NULL,
  created INTEGER NOT NULL DEFAULT 0,
  updated INTEGER NOT NULL DEFAULT 0,
  first_lot INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (op_id,batch_no)
) STRICT, WITHOUT ROWID;

PRAGMA user_version = 22;
