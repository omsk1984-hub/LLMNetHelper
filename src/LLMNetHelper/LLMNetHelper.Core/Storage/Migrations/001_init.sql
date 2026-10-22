-- Миграция 001: начальная схема БД для ночного агента
-- WAL-режим включается при инициализации подключения.

CREATE TABLE IF NOT EXISTS runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    window_from TEXT NOT NULL,
    window_to TEXT NOT NULL,
    status TEXT NOT NULL,
    verdict TEXT,
    llm_provider TEXT,
    error TEXT
);

CREATE TABLE IF NOT EXISTS log_facts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    bucket_start TEXT NOT NULL,
    job TEXT,
    svc TEXT,
    lvl TEXT,
    logger TEXT,
    symbol TEXT,
    count INTEGER NOT NULL,
    UNIQUE(run_id, bucket_start, job, svc, lvl, logger, symbol)
);

CREATE TABLE IF NOT EXISTS error_samples (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    signature TEXT NOT NULL,
    lvl TEXT NOT NULL,
    job TEXT,
    logger TEXT,
    symbol TEXT,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    count INTEGER NOT NULL,
    sample_text TEXT NOT NULL,
    UNIQUE(run_id, signature)
);

CREATE TABLE IF NOT EXISTS metric_facts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    name TEXT NOT NULL,
    labels TEXT,
    value REAL,
    slope_per_hour REAL,
    r2 REAL,
    window_from TEXT,
    window_to TEXT
);

CREATE TABLE IF NOT EXISTS llm_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    provider TEXT NOT NULL,
    endpoint TEXT,
    model TEXT,
    prompt_tokens INTEGER,
    completion_tokens INTEGER,
    duration_ms INTEGER,
    finish_reason TEXT,
    fallback_used INTEGER NOT NULL DEFAULT 0,
    raw_response TEXT
);

CREATE INDEX IF NOT EXISTS ix_error_samples_run ON error_samples(run_id, count DESC);
CREATE INDEX IF NOT EXISTS ix_log_facts_run_bucket ON log_facts(run_id, bucket_start);
CREATE INDEX IF NOT EXISTS ix_runs_started ON runs(started_at DESC);
