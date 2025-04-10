{
  "Ghost": {
    "ServiceName": "{{ safe_name }}",
    "Environment": "Development",
    "Logging": {
      "Level": "Information",
      "OutputPath": "logs",
      "RetentionDays": 30,
      "EnableConsole": true,
      "EnableFile": true
    },
    "Features": {
      "Monitoring": true,
      "DistributedCache": false,
      "BackgroundJobs": true
    }
  },
  "ServiceSettings": {
    "WorkerInterval": 10000,
    "MaxConcurrency": 4,
    "RetryLimit": 3,
    "RetryDelayMs": 1000
  },
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=data/service.db"
  }
}