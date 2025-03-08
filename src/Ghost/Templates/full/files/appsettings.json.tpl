{
  "Ghost": {
    "ServiceName": "{{ safe_name }}",
    "Environment": "Development",
    "Logging": {
      "Level": "Information",
      "OutputPath": "logs",
      "RetentionDays": 7,
      "EnableConsole": true,
      "EnableFile": true
    },
    "Features": {
      "Monitoring": true,
      "DistributedCache": false,
      "BackgroundJobs": true
    }
  }
}