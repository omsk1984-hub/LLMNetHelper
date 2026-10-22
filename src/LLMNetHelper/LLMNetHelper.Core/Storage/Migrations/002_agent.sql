-- Миграция 002: агентские таблицы (диагноз, гипотезы, правки, инструменты, контекст).
-- Совместима с уже существующей 001_init.sql; применяется после неё.

-- Диагнозы: результат работы оркестратора.
CREATE TABLE IF NOT EXISTS diagnoses (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    verdict TEXT NOT NULL,
    summary TEXT NOT NULL,
    orchestrator TEXT NOT NULL,
    input_tokens INTEGER,
    output_tokens INTEGER,
    tool_calls INTEGER NOT NULL DEFAULT 0,
    duration_ms INTEGER
);

-- Гипотезы о корневой причине.
CREATE TABLE IF NOT EXISTS hypotheses (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    diagnosis_id INTEGER NOT NULL REFERENCES diagnoses(id),
    root_cause TEXT NOT NULL,
    confidence TEXT NOT NULL,
    evidence TEXT NOT NULL
);

-- Прочитанный внешний контекст (для воспроизводимости прогона).
CREATE TABLE IF NOT EXISTS context_reads (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    source TEXT NOT NULL,
    doc_id TEXT NOT NULL,
    chars INTEGER NOT NULL DEFAULT 0,
    score REAL
);

-- Предложенные и применённые правки кода.
CREATE TABLE IF NOT EXISTS code_changes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    sequence INTEGER NOT NULL DEFAULT 0,
    file_path TEXT NOT NULL,
    symbol_name TEXT NOT NULL,
    change_kind TEXT NOT NULL,
    new_code TEXT NOT NULL,
    status TEXT NOT NULL,
    failure_reason TEXT
);

-- Ветки, созданные агентом для правок.
CREATE TABLE IF NOT EXISTS change_branches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    branch_name TEXT NOT NULL,
    base_branch TEXT NOT NULL,
    commit_sha TEXT,
    build_ok INTEGER,
    tests_ok INTEGER,
    created_at TEXT NOT NULL
);

-- Вызовы инструментов агентом (для аудита и подсчёта токенов).
CREATE TABLE IF NOT EXISTS tool_calls (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(id),
    tool_name TEXT NOT NULL,
    arguments TEXT,
    result_chars INTEGER,
    duration_ms INTEGER,
    succeeded INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_hypotheses_diag ON hypotheses(diagnosis_id);
CREATE INDEX IF NOT EXISTS ix_code_changes_run ON code_changes(run_id, sequence);
CREATE INDEX IF NOT EXISTS ix_tool_calls_run ON tool_calls(run_id);
CREATE INDEX IF NOT EXISTS ix_context_reads_run ON context_reads(run_id);
CREATE INDEX IF NOT EXISTS ix_change_branches_run ON change_branches(run_id);
