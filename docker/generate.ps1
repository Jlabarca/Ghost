# Clean up existing containers and images to avoid conflicts
Write-Host "Cleaning up existing Docker containers and images..."
try {
    docker-compose down --remove-orphans
    $containers = docker ps -a -q -f "name=ghost-*"
    if ($containers) {
        docker rm -f $containers
    }
    $images = @(
        "redis:7-alpine",
        "postgres:15-alpine",
        "oliver006/redis_exporter",
        "prometheuscommunity/postgres-exporter",
        "prom/prometheus:latest",
        "grafana/grafana:latest",
        "prom/node-exporter:latest",
        "dpage/pgadmin4:latest",
        "redis/redisinsight:latest"
    )
    foreach ($image in $images) {
        $imageId = docker images -q $image
        if ($imageId) {
            docker rmi -f $imageId
        }
    }
} catch {
    Write-Warning "Cleanup failed, but continuing with setup. Some conflicts may occur."
}

# Verify Docker is running
Write-Host "Checking Docker status..."
try {
    $dockerVersion = docker version --format '{{.Server.Version}}'
    Write-Host "Docker is running, version: $dockerVersion"
} catch {
    Write-Error "Docker Desktop is not running or not accessible. Please start Docker Desktop and ensure it is configured correctly."
    exit 1
}

# Verify Docker Compose is installed
Write-Host "Checking Docker Compose..."
try {
    $composeVersion = docker-compose --version
    Write-Host "Docker Compose is installed: $composeVersion"
} catch {
    Write-Error "Docker Compose is not installed or not accessible. Please ensure it is installed."
    exit 1
}

# Verify network connectivity
Write-Host "Checking network connectivity to Docker Hub..."
try {
    $response = Invoke-WebRequest -Uri "https://hub.docker.com" -Method Head -UseBasicParsing
    if ($response.StatusCode -eq 200) {
        Write-Host "Network connectivity to Docker Hub is OK"
    }
} catch {
    Write-Error "Failed to connect to Docker Hub. Please check your internet connection."
    exit 1
}

# Create docker directory structure
New-Item -ItemType Directory -Path "docker\config","docker\scripts" -Force
Set-Location docker

# Create docker-compose.yml with corrected node-exporter image
@"
services:
  redis:
    image: redis:7-alpine
    container_name: ghost-redis
    ports:
      - "6379:6379"
    volumes:
      - redis_data:/data
      - ./config/redis.conf:/usr/local/etc/redis/redis.conf
    command: redis-server /usr/local/etc/redis/redis.conf
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5
    networks:
      - monitoring

  postgres:
    image: postgres:15-alpine
    container_name: ghost-postgres
    environment:
      POSTGRES_DB: ghost
      POSTGRES_USER: ghost
      POSTGRES_PASSWORD: ghost
      POSTGRES_INITDB_ARGS: "--encoding=UTF8 --locale=C"
    ports:
      - "5432:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./scripts/init-db.sql:/docker-entrypoint-initdb.d/01-init-db.sql
      - ./scripts/init-monitoring.sql:/docker-entrypoint-initdb.d/02-monitoring.sql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ghost"]
      interval: 10s
      timeout: 5s
      retries: 5
    networks:
      - monitoring

  redis-exporter:
    image: oliver006/redis_exporter
    container_name: ghost-redis-exporter
    ports:
      - "9121:9121"
    environment:
      REDIS_ADDR: redis:6379
    depends_on:
      - redis
    networks:
      - monitoring

  postgres-exporter:
    image: prometheuscommunity/postgres-exporter
    container_name: ghost-postgres-exporter
    ports:
      - "9187:9187"
    environment:
      DATA_SOURCE_NAME: "postgresql://ghost:ghost@postgres:5432/ghost?sslmode=disable"
    depends_on:
      - postgres
    networks:
      - monitoring

  prometheus:
    image: prom/prometheus:latest
    container_name: prometheus
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
      - prometheus_data:/prometheus
    depends_on:
      - postgres-exporter
      - redis-exporter
      - node-exporter
    networks:
      - monitoring

  grafana:
    image: grafana/grafana:latest
    container_name: grafana
    ports:
      - "3000:3000"
    environment:
      - GF_SECURITY_ADMIN_USER=admin
      - GF_SECURITY_ADMIN_PASSWORD=admin
    volumes:
      - grafana_data:/var/lib/grafana
    depends_on:
      - prometheus
    networks:
      - monitoring

  node-exporter:
    image: prom/node-exporter:latest
    container_name: node-exporter
    ports:
      - "9100:9100"
    volumes:
      - /proc:/host/proc:ro
      - /sys:/host/sys:ro
      - /:/rootfs:ro
    command:
      - '--path.procfs=/host/proc'
      - '--path.sysfs=/host/sys'
      - '--collector.filesystem.ignored-mount-points=^/(sys|proc|dev|host|etc)($$|/)'
    networks:
      - monitoring

  pgadmin:
    image: dpage/pgadmin4:latest
    container_name: pgadmin
    ports:
      - "5050:80"
    environment:
      - PGADMIN_DEFAULT_EMAIL=admin@admin.com
      - PGADMIN_DEFAULT_PASSWORD=admin
    depends_on:
      - postgres
    networks:
      - monitoring

  redisinsight:
    image: redis/redisinsight:latest
    container_name: redisinsight
    ports:
      - "5540:5540"
    depends_on:
      - redis
    networks:
      - monitoring

volumes:
  redis_data:
  postgres_data:
  prometheus_data:
  grafana_data:

networks:
  monitoring:
    driver: bridge
"@ | Out-File -FilePath "docker-compose.yml" -Encoding UTF8

# Create Prometheus configuration
@"
global:
  scrape_interval: 15s
  evaluation_interval: 15s

scrape_configs:
  - job_name: 'prometheus'
    static_configs:
      - targets: ['localhost:9090']
  - job_name: 'postgres-exporter'
    static_configs:
      - targets: ['ghost-postgres-exporter:9187']
  - job_name: 'redis-exporter'
    static_configs:
      - targets: ['ghost-redis-exporter:9121']
  - job_name: 'node-exporter'
    static_configs:
      - targets: ['node-exporter:9100']
"@ | Out-File -FilePath "prometheus.yml" -Encoding UTF8

# Create Redis configuration
@"
# Persistence
appendonly yes
appendfsync everysec

# Memory management
maxmemory 2gb
maxmemory-policy allkeys-lru

# Performance
save 900 1
save 300 10
save 60 10000

# Logging
loglevel notice
"@ | Out-File -FilePath "config/redis.conf" -Encoding UTF8

# Create database initialization script (init-db.sql)
@"
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
"@ | Out-File -FilePath "scripts/init-db.sql" -Encoding UTF8

# Create monitoring setup (init-monitoring.sql)
@"
-- Create monitoring schema
CREATE SCHEMA IF NOT EXISTS monitoring;

-- Performance metrics table
CREATE TABLE monitoring.performance_metrics (
    id BIGSERIAL PRIMARY KEY,
    metric_name TEXT NOT NULL,
    metric_value NUMERIC NOT NULL,
    tags JSONB DEFAULT '{}',
    timestamp TIMESTAMPTZ DEFAULT NOW()
) PARTITION BY RANGE (timestamp);

-- Create partition for performance metrics
CREATE TABLE monitoring.performance_metrics_template (LIKE monitoring.performance_metrics INCLUDING ALL);
ALTER TABLE monitoring.performance_metrics ATTACH PARTITION monitoring.performance_metrics_template FOR VALUES FROM (MINVALUE) TO (MAXVALUE);

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
"@ | Out-File -FilePath "scripts/init-monitoring.sql" -Encoding UTF8

# Pull images to verify availability
Write-Host "Verifying image availability..."
$images = @(
    "redis:7-alpine",
    "postgres:15-alpine",
    "oliver006/redis_exporter",
    "prometheuscommunity/postgres-exporter",
    "prom/prometheus:latest",
    "grafana/grafana:latest",
    "prom/node-exporter:latest",
    "dpage/pgadmin4:latest",
    "redis/redisinsight:latest"
)
foreach ($image in $images) {
    Write-Host "Pulling $image..."
    try {
        docker pull $image
    } catch {
        Write-Error "Failed to pull $image. Please check Docker Hub access, internet connectivity, or log in with 'docker login'."
        exit 1
    }
}

# Start the services with increased timeout
Write-Host "Starting Docker Compose services..."
try {
    # Set environment variable for increased timeout (COMPOSE_HTTP_TIMEOUT in seconds)
    $env:COMPOSE_HTTP_TIMEOUT = "120"
    docker-compose up -d
} catch {
    Write-Error "Failed to start Docker Compose services. Please check Docker Desktop, network, and the docker-compose.yml file."
    exit 1
}

# Wait for services to be healthy
Write-Host "Waiting for services to be healthy..."
Start-Sleep -Seconds 60

# Verify services
Write-Host "Verifying services..."
try {
    docker-compose ps
} catch {
    Write-Error "Failed to verify Docker Compose services. Please check the service status with 'docker-compose logs'."
    exit 1
}

# Output instructions for accessing services
Write-Host ""
Write-Host "Setup complete! Access the services at:"
Write-Host "  - Prometheus: http://localhost:9090"
Write-Host "  - Grafana: http://localhost:3000 (login: admin/admin)"
Write-Host "  - pgAdmin: http://localhost:5050 (email: admin@admin.com, password: admin)"
Write-Host "  - redisInsight: http://localhost:5540"
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. In Grafana, add Prometheus as a data source (URL: http://prometheus:9090)."
Write-Host "2. Import dashboards:"
Write-Host "   - PostgreSQL: ID 9628"
Write-Host "   - Redis: ID 763 or 14239"
Write-Host "   - Node Exporter: ID 1860"
Write-Host "3. In pgAdmin, add PostgreSQL server (host: postgres, port: 5432, user: ghost, password: ghost)."
Write-Host "4. In redisInsight, add Redis instance (host: redis, port: 6379)."
Write-Host "5. If issues persist, check logs with 'docker-compose logs' or restart Docker Desktop."