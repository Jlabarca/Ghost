-- Enable extensions
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pg_stat_statements";

-- Core process management with partitioning
CREATE TABLE processes (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    type TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Starting', 'Running', 'Stopping', 'Stopped', 'Failed', 'Crashed')),
    config JSONB NOT NULL DEFAULT '{}',
    metrics JSONB DEFAULT '{}',
    last_heartbeat TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Process events with partitioning
CREATE TABLE process_events (
    id BIGSERIAL,
    process_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_data BYTEA,
    timestamp TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (id, timestamp),
    FOREIGN KEY (process_id) REFERENCES processes(id) ON DELETE CASCADE
) PARTITION BY RANGE (timestamp);

-- Create partitions for the next 12 months
DO $$
DECLARE
    start_date DATE := DATE_TRUNC('month', CURRENT_DATE);
    partition_date DATE;
    partition_name TEXT;
BEGIN
    FOR i IN 0..11 LOOP
        partition_date := start_date + (i || ' months')::INTERVAL;
        partition_name := 'process_events_' || TO_CHAR(partition_date, 'YYYY_MM');
        
        EXECUTE format(
            'CREATE TABLE %I PARTITION OF process_events
             FOR VALUES FROM (%L) TO (%L)',
            partition_name,
            partition_date,
            partition_date + INTERVAL '1 month'
        );
    END LOOP;
END $$;

-- Process groups for orchestration
CREATE TABLE process_groups (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    config JSONB NOT NULL DEFAULT '{}',
    dependencies JSONB DEFAULT '{}',
    status TEXT NOT NULL DEFAULT 'Created',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Process group members
CREATE TABLE process_group_members (
    group_id TEXT NOT NULL,
    process_id TEXT NOT NULL,
    role TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (group_id, process_id),
    FOREIGN KEY (group_id) REFERENCES process_groups(id) ON DELETE CASCADE,
    FOREIGN KEY (process_id) REFERENCES processes(id) ON DELETE CASCADE
);

-- Configuration storage with versioning
CREATE TABLE ghost_config (
    id TEXT NOT NULL,
    config JSONB NOT NULL,
    version INTEGER NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    created_by TEXT,
    PRIMARY KEY (id, version)
);

-- Shared data storage with encryption support
CREATE TABLE ghost_data (
    key TEXT PRIMARY KEY,
    value BYTEA,
    metadata JSONB DEFAULT '{}',
    owner_id TEXT,
    encrypted BOOLEAN DEFAULT FALSE,
    expires_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Sagas for distributed transactions
CREATE TABLE sagas (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'Started',
    data JSONB DEFAULT '{}',
    compensations JSONB DEFAULT '[]',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    completed_at TIMESTAMPTZ
);

-- Create indexes
CREATE INDEX idx_process_status ON processes(status);
CREATE INDEX idx_process_type ON processes(type);
CREATE INDEX idx_process_heartbeat ON processes(last_heartbeat);
CREATE INDEX idx_process_events_process_id ON process_events(process_id);
CREATE INDEX idx_process_events_type ON process_events(event_type);
CREATE INDEX idx_process_events_timestamp ON process_events(timestamp);
CREATE INDEX idx_ghost_data_owner ON ghost_data(owner_id);
CREATE INDEX idx_ghost_data_expires ON ghost_data(expires_at);
CREATE INDEX idx_ghost_data_encrypted ON ghost_data(encrypted);
CREATE INDEX idx_process_groups_status ON process_groups(status);
CREATE INDEX idx_sagas_state ON sagas(state);
CREATE INDEX idx_sagas_created_at ON sagas(created_at);

-- Create update trigger for updated_at
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ language 'plpgsql';

CREATE TRIGGER update_processes_updated_at
    BEFORE UPDATE ON processes
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

CREATE TRIGGER update_process_groups_updated_at
    BEFORE UPDATE ON process_groups
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

CREATE TRIGGER update_ghost_data_updated_at
    BEFORE UPDATE ON ghost_data
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();
