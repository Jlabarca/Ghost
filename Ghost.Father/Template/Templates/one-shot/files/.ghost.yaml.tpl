app:
  id: "{{ safe_name }}"
  name: "{{ project_name }}"
  description: "{{ defaultDescription }}"
  version: "1.0.0"

core:
  mode: "development"
  dataPath: "data"
  logsPath: "logs"

  # One-shot apps have different settings
  healthCheckInterval: "00:00:05"  # Shorter interval for quicker response
  metricsInterval: "00:00:01"      # Fewer metrics for one-shot apps

  settings:
    autoGhostFather: "true"
    autoMonitor: "true"
    isService: "false"  # This is a one-shot app
    autoRestart: "false"
    maxRestartAttempts: "0"

modules:
  logging:
    enabled: true
    provider: "file"
    options:
      path: "logs"
      level: "Information"

  cache:
    enabled: true
    provider: "memory"
    options:
      maxSize: "100MB"