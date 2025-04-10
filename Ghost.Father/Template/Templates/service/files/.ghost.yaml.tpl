app:
  id: "{{ safe_name }}"
  name: "{{ project_name }}"
  description: "{{ defaultDescription }}"
  version: "1.0.0"

core:
  mode: "development"
  dataPath: "data"
  logsPath: "logs"

  # Service settings for monitoring and resilience
  healthCheckInterval: "00:00:30"
  metricsInterval: "00:00:05"

  settings:
    autoGhostFather: "true"
    autoMonitor: "true"
    isService: "true"     # This is a long-running service
    autoRestart: "true"   # Auto-restart on failure
    maxRestartAttempts: "3"
    tickInterval: "5"     # Tick interval in seconds

modules:
  logging:
    enabled: true
    provider: "file"
    options:
      path: "logs"
      level: "Information"
      retentionDays: 30

  cache:
    enabled: true
    provider: "memory"
    options:
      maxSize: "200MB"

  monitoring:
    enabled: true
    provider: "ghost"
    options:
      interval: "00:00:05"
      retentionDays: 7