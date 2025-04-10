{
  "Ghost": {
    "AppName": "{{ safe_name }}",
    "Environment": "Development",
    "Logging": {
      "Level": "Information",
      "OutputPath": "logs",
      "RetentionDays": 7,
      "EnableConsole": true,
      "EnableFile": true
    },
    "Features": {
      "Monitoring": false,
      "DistributedCache": false,
      "BackgroundJobs": false
    }
  },
  "ApplicationSettings": {
    "DefaultTimeout": 30000,
    "MaxConcurrentOperations": 4
  }
}