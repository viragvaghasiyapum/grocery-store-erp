CREATE TABLE print_job (
    id INTEGER PRIMARY KEY,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    state TEXT NOT NULL CHECK (state IN ('PENDING','SENDING','REVIEW','FAILED')),
    document TEXT NOT NULL,
    payload TEXT NOT NULL
);

CREATE INDEX ix_print_job_review ON print_job(state, id);
