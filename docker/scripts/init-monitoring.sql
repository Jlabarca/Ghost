-- Create monitoring schema
CREATE SCHEMA IF NOT EXISTS monitoring;

-- Performance metrics table
CREATE TABLE monitoring.performance_metrics (
    id BIGSERIAL,  -- Removed PRIMARY KEY here
    metric_name TEXT NOT NULL,
    metric_value NUMERIC NOT NULL,
    tags JSONB DEFAULT '{}',
    timestamp TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (id, timestamp)  -- Primary key must include timestamp (partition key)
) PARTITION BY RANGE (timestamp);

-- Create partition for performance metrics
CREATE TABLE monitoring.performance_metrics_template
 (LIKE monitoring.performance_metrics INCLUDING ALL);
ALTER TABLE monitoring.performance_metrics ATTACH PARTITION monitoring.performance_metrics_template 
FOR VALUES FROM (MINVALUE) TO (MAXVALUE);

-- Create monitoring views
CREATE VIEW monitoring.active_processes AS
SELECT id, name, type, status, last_heartbeat,
       EXTRACT(EPOCH FROM (NOW() - last_heartbeat)) as seconds_since_heartbeat
FROM processes
WHERE status = 'Running';

CREATE VIEW monitoring.process_event_stats AS
SELECT process_id, event_type, COUNT(*) as event_count,
       MIN(timestamp) as first_event,
       MAX(timestamp) as last_event
FROM process_events
GROUP BY process_id, event_type;

-- Create monitoring functions
CREATE OR REPLACE FUNCTION monitoring.get_process_health()
RETURNS TABLE (
    process_id TEXT,
    name TEXT,
    status TEXT,
    health_status TEXT,
    last_heartbeat TIMESTAMPTZ,
    seconds_since_heartbeat NUMERIC
) AS $$
BEGIN
    RETURN QUERY
    SELECT 
        p.id,
        p.name,
        p.status,
        CASE 
            WHEN p.status != 'Running' THEN 'Not Running'
            WHEN p.last_heartbeat IS NULL THEN 'Unknown'
            WHEN EXTRACT(EPOCH FROM (NOW() - p.last_heartbeat)) > 120 THEN 'Unhealthy'
            WHEN EXTRACT(EPOCH FROM (NOW() - p.last_heartbeat)) > 60 THEN 'Warning'
            ELSE 'Healthy'
        END as health_status,
        p.last_heartbeat,
        EXTRACT(EPOCH FROM (NOW() - p.last_heartbeat)) as seconds_since_heartbeat
    FROM processes p;
END;
$$ LANGUAGE plpgsql;