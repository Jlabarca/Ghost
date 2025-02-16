app:
  id: "{{ safe_name }}"
  name: "{{ project_name }}"
  description: "{{ defaultDescription }}"
  version: "1.0.0"

core:
  healthCheckInterval: "00:00:30"
  metricsInterval: "00:00:05"
  dataDirectory: "data"

modules:
  logging:
    enabled: true
    provider: "file"
    options:
      path: "logs"
      level: "Information"