# Ghost SDK Boot Script
# This script starts the Ghost SDK infrastructure using existing configuration files

param (
    [switch]$CleanExisting = $false,
    [switch]$SkipPull = $false,
    [int]$WaitTime = 60,
    [switch]$SkipValidation = $false
)

# Display banner
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "   Ghost SDK Infrastructure Boot Script" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

# Verify required files exist
$requiredFiles = @(
    "docker-compose.yml",
    "prometheus.yml",
    "config/redis.conf",
    "scripts/init-db.sql",
    "scripts/init-monitoring.sql"
)

$missingFiles = @()
foreach ($file in $requiredFiles) {
    if (-not (Test-Path $file)) {
        $missingFiles += $file
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Error "The following required files are missing:"
    foreach ($file in $missingFiles) {
        Write-Error "  - $file"
    }
    Write-Error "Please ensure all required files are present or run the full setup script (run.ps1)"
    exit 1
}

# Clean up existing containers if requested
if ($CleanExisting) {
    Write-Host "Cleaning up existing Docker containers..." -ForegroundColor Yellow
    try {
        docker-compose down --remove-orphans
        $containers = docker ps -a -q -f "name=ghost-*"
        if ($containers) {
            docker rm -f $containers
        }
        Write-Host "Cleanup completed successfully." -ForegroundColor Green
    } catch {
        Write-Warning "Cleanup encountered issues, but continuing with setup. Some conflicts may occur."
    }
}

# Verify Docker is running
Write-Host "Checking Docker status..." -ForegroundColor Yellow
try {
    $dockerVersion = docker version --format '{{.Server.Version}}'
    Write-Host "Docker is running, version: $dockerVersion" -ForegroundColor Green
} catch {
    Write-Error "Docker Desktop is not running or not accessible. Please start Docker Desktop and ensure it is configured correctly."
    exit 1
}

# Verify Docker Compose is installed
Write-Host "Checking Docker Compose..." -ForegroundColor Yellow
try {
    $composeVersion = docker-compose --version
    Write-Host "Docker Compose is installed: $composeVersion" -ForegroundColor Green
} catch {
    Write-Error "Docker Compose is not installed or not accessible. Please ensure it is installed."
    exit 1
}

# Verify network connectivity
Write-Host "Checking network connectivity to Docker Hub..." -ForegroundColor Yellow
try {
    $response = Invoke-WebRequest -Uri "https://hub.docker.com" -Method Head -UseBasicParsing
    if ($response.StatusCode -eq 200) {
        Write-Host "Network connectivity to Docker Hub is OK" -ForegroundColor Green
    }
} catch {
    Write-Warning "Failed to connect to Docker Hub. Continuing, but you may experience issues pulling images."
}

# Pull images if not skipped
if (-not $SkipPull) {
    Write-Host "Pulling required Docker images..." -ForegroundColor Yellow
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
    
    $pullSucceeded = $true
    foreach ($image in $images) {
        Write-Host "  Pulling $image..." -ForegroundColor DarkYellow
        try {
            docker pull $image
        } catch {
            Write-Warning "Failed to pull $image. Will attempt to continue with locally cached images."
            $pullSucceeded = $false
        }
    }
    
    if ($pullSucceeded) {
        Write-Host "All images pulled successfully." -ForegroundColor Green
    } else {
        Write-Warning "Some images could not be pulled. Continuing with local images if available."
    }
}

# Start the services
Write-Host "Starting Docker Compose services..." -ForegroundColor Yellow
try {
    # Set environment variable for increased timeout
    $env:COMPOSE_HTTP_TIMEOUT = "120"
    docker-compose up -d
    Write-Host "Services started successfully." -ForegroundColor Green
} catch {
    Write-Error "Failed to start Docker Compose services: $_"
    Write-Error "Please check Docker Desktop, network connection, and the docker-compose.yml file."
    exit 1
}

# Wait for services
Write-Host "Waiting $WaitTime seconds for services to initialize..." -ForegroundColor Yellow
for ($i = 1; $i -le $WaitTime; $i++) {
    Write-Progress -Activity "Waiting for services to initialize" -Status "$i/$WaitTime seconds elapsed" -PercentComplete (($i / $WaitTime) * 100)
    Start-Sleep -Seconds 1
}
Write-Progress -Activity "Waiting for services to initialize" -Completed

# Verify services
Write-Host "Verifying service status..." -ForegroundColor Yellow
try {
    $services = docker-compose ps
    Write-Host "Services status:" -ForegroundColor Yellow
    Write-Host $services
    
    # Count running services
    $runningServices = docker-compose ps --services --filter "status=running" | Measure-Object | Select-Object -ExpandProperty Count
    $totalServices = docker-compose config --services | Measure-Object | Select-Object -ExpandProperty Count
    
    if ($runningServices -eq $totalServices) {
        Write-Host "All services are running ($runningServices/$totalServices)." -ForegroundColor Green
    } else {
        Write-Warning "Only $runningServices out of $totalServices services are running."
        Write-Host "You may need to check individual services with 'docker-compose logs <service-name>'."
    }
} catch {
    Write-Warning "Failed to verify Docker Compose services. Please check manually with 'docker-compose ps'."
}

# Validate infrastructure setup if not skipped
if (-not $SkipValidation) {
    Write-Host ""
    Write-Host "Validating infrastructure..." -ForegroundColor Yellow
    
    # Function to validate PostgreSQL setup
    function ValidatePostgres {
        Write-Host "  Validating PostgreSQL database..." -ForegroundColor Yellow
        try {
            # Check if postgres container is running
            $pgStatus = docker ps -q -f "name=ghost-postgres" -f "status=running"
            if (-not $pgStatus) {
                Write-Warning "    PostgreSQL container is not running"
                return $false
            }
            
            # Check if postgres is accepting connections
            $pgIsReady = docker exec ghost-postgres pg_isready -U ghost
            if (-not $?) {
                Write-Warning "    PostgreSQL is not accepting connections"
                return $false
            }
            
            # Check if tables are created
            $coreTables = @(
                "processes",
                "process_events",
                "process_groups",
                "ghost_config",
                "ghost_data",
                "sagas"
            )
            
            $missingTables = @()
            foreach ($table in $coreTables) {
                $result = docker exec ghost-postgres psql -U ghost -d ghost -t -c "SELECT to_regclass('public.$table');"
                if (-not $result -or $result.Trim() -eq "") {
                    $missingTables += $table
                }
            }
            
            if ($missingTables.Count -gt 0) {
                Write-Warning "    Missing tables: $($missingTables -join ', ')"
                return $false
            }
            
            # Check for partitioned tables
            $partitionResult = docker exec ghost-postgres psql -U ghost -d ghost -t -c "SELECT count(*) FROM pg_inherits WHERE inhparent = 'process_events'::regclass;"
            $partitionCount = 0
            
            # Properly parse the output which might be an array of strings with whitespace
            if ($partitionResult -is [array]) {
                $countString = ($partitionResult | Where-Object { $_ -match '\d' } | Select-Object -First 1)
                if ($countString) {
                    $partitionCount = [int]($countString -replace '[^0-9]', '')
                }
            } else {
                # If it's not an array, try direct conversion
                $partitionCount = [int]($partitionResult -replace '[^0-9]', '')
            }
            
            Write-Host "    Found $partitionCount partitions for process_events table"
            
            if ($partitionCount -lt 1) {
                Write-Warning "    Missing partitions for process_events table"
                return $false
            }
            
            # Check monitoring schema
            $monitoringSchema = docker exec ghost-postgres psql -U ghost -d ghost -t -c "SELECT schema_name FROM information_schema.schemata WHERE schema_name = 'monitoring';"
            if (-not $monitoringSchema -or $monitoringSchema.Trim() -eq "") {
                Write-Warning "    Monitoring schema is missing"
                return $false
            }
            
            # If we got here, all is good
            Write-Host "    PostgreSQL database validation passed!" -ForegroundColor Green
            return $true
        } catch {
            Write-Warning "    PostgreSQL validation error: $_"
            return $false
        }
    }
    
    # Function to validate Redis setup
    function ValidateRedis {
        Write-Host "  Validating Redis..." -ForegroundColor Yellow
        try {
            # Check if redis container is running
            $redisStatus = docker ps -q -f "name=ghost-redis" -f "status=running"
            if (-not $redisStatus) {
                Write-Warning "    Redis container is not running"
                return $false
            }
            
            # Test redis connection
            $redisPing = docker exec ghost-redis redis-cli ping
            if ($redisPing -ne "PONG") {
                Write-Warning "    Redis is not responding to ping"
                return $false
            }
            
            # Check redis configuration
            $redisConfig = docker exec ghost-redis redis-cli config get maxmemory-policy
            if (-not $redisConfig -or $redisConfig[1] -ne "allkeys-lru") {
                Write-Warning "    Redis configuration is not properly applied"
                return $false
            }
            
            # If we got here, all is good
            Write-Host "    Redis validation passed!" -ForegroundColor Green
            return $true
        } catch {
            Write-Warning "    Redis validation error: $_"
            return $false
        }
    }
    
    # Function to validate Prometheus setup
    function ValidatePrometheus {
        Write-Host "  Validating Prometheus..." -ForegroundColor Yellow
        try {
            # Check if prometheus container is running
            $prometheusStatus = docker ps -q -f "name=prometheus" -f "status=running"
            if (-not $prometheusStatus) {
                Write-Warning "    Prometheus container is not running"
                return $false
            }
            
            # Check if prometheus is responding
            try {
                $response = Invoke-WebRequest -Uri "http://localhost:9090/-/healthy" -UseBasicParsing
                if ($response.StatusCode -ne 200) {
                    Write-Warning "    Prometheus is not healthy"
                    return $false
                }
            } catch {
                Write-Warning "    Prometheus is not responding"
                return $false
            }
            
            # If we got here, all is good
            Write-Host "    Prometheus validation passed!" -ForegroundColor Green
            return $true
        } catch {
            Write-Warning "    Prometheus validation error: $_"
            return $false
        }
    }
    
    # Function to validate Grafana setup
    function ValidateGrafana {
        Write-Host "  Validating Grafana..." -ForegroundColor Yellow
        try {
            # Check if grafana container is running
            $grafanaStatus = docker ps -q -f "name=grafana" -f "status=running"
            if (-not $grafanaStatus) {
                Write-Warning "    Grafana container is not running"
                return $false
            }
            
            # Check if grafana is responding
            try {
                $response = Invoke-WebRequest -Uri "http://localhost:3000/api/health" -UseBasicParsing
                if ($response.StatusCode -ne 200) {
                    Write-Warning "    Grafana is not healthy"
                    return $false
                }
            } catch {
                Write-Warning "    Grafana is not responding"
                return $false
            }
            
            # If we got here, all is good
            Write-Host "    Grafana validation passed!" -ForegroundColor Green
            return $true
        } catch {
            Write-Warning "    Grafana validation error: $_"
            return $false
        }
    }
    
    # Run validations
    $postgresValid = ValidatePostgres
    $redisValid = ValidateRedis
    $prometheusValid = ValidatePrometheus
    $grafanaValid = ValidateGrafana
    
    # Print detailed validation results
    Write-Host ""
    Write-Host "Validation Summary:" -ForegroundColor Cyan
    Write-Host "  PostgreSQL:  $([char]0x25cf) $(if($postgresValid){"PASSED"}else{"FAILED"})" -ForegroundColor $(if($postgresValid){[ConsoleColor]::Green}else{[ConsoleColor]::Red})
    Write-Host "  Redis:       $([char]0x25cf) $(if($redisValid){"PASSED"}else{"FAILED"})" -ForegroundColor $(if($redisValid){[ConsoleColor]::Green}else{[ConsoleColor]::Red})
    Write-Host "  Prometheus:  $([char]0x25cf) $(if($prometheusValid){"PASSED"}else{"FAILED"})" -ForegroundColor $(if($prometheusValid){[ConsoleColor]::Green}else{[ConsoleColor]::Red})
    Write-Host "  Grafana:     $([char]0x25cf) $(if($grafanaValid){"PASSED"}else{"FAILED"})" -ForegroundColor $(if($grafanaValid){[ConsoleColor]::Green}else{[ConsoleColor]::Red})
    
    # Overall validation status
    $allValid = $postgresValid -and $redisValid -and $prometheusValid -and $grafanaValid
    Write-Host ""
    if ($allValid) {
        Write-Host "VALIDATION SUCCESSFUL: All components are properly configured and running!" -ForegroundColor Green
    } else {
        Write-Host "VALIDATION WARNING: Some components may not be properly configured." -ForegroundColor Yellow
        Write-Host "  You can continue, but some Ghost SDK features may not work correctly." -ForegroundColor Yellow
        Write-Host "  Check the specific component logs with: docker-compose logs <service-name>" -ForegroundColor Yellow
    }
    Write-Host ""
}

# Show detailed PostgreSQL schema if validation was successful or skipped
# Store validation result in a variable to avoid running validation twice
$postgresValidationResult = ValidatePostgres
if (-not $SkipValidation -and $postgresValidationResult) {
    Write-Host "PostgreSQL Schema Details:" -ForegroundColor Cyan
    try {
        $schema = docker exec ghost-postgres psql -U ghost -d ghost -c "\dt"
        Write-Host $schema -ForegroundColor Gray
        
        Write-Host "Partition details:" -ForegroundColor Cyan
        $partitions = docker exec ghost-postgres psql -U ghost -d ghost -c "SELECT inhrelid::regclass FROM pg_inherits WHERE inhparent = 'process_events'::regclass;"
        Write-Host $partitions -ForegroundColor Gray
    } catch {
        Write-Warning "Could not retrieve detailed schema information"
    }
}

# Output access information
Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "   Ghost SDK Infrastructure is ready!" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Access the services at:" -ForegroundColor White
Write-Host "  - Prometheus: http://localhost:9090" -ForegroundColor Green
Write-Host "  - Grafana: http://localhost:3000 (login: admin/admin)" -ForegroundColor Green
Write-Host "  - pgAdmin: http://localhost:5050 (email: admin@admin.com, password: admin)" -ForegroundColor Green
Write-Host "  - RedisInsight: http://localhost:5540" -ForegroundColor Green
Write-Host ""
Write-Host "Database Connection Information:" -ForegroundColor White
Write-Host "  - PostgreSQL: Host=localhost; Port=5432; Database=ghost; Username=ghost; Password=ghost" -ForegroundColor Yellow
Write-Host "  - Redis: Host=localhost; Port=6379" -ForegroundColor Yellow
Write-Host ""
Write-Host "Quick Commands:" -ForegroundColor White
Write-Host "  - View service logs: docker-compose logs [service-name]" -ForegroundColor Yellow
Write-Host "  - Stop all services: docker-compose down" -ForegroundColor Yellow
Write-Host "  - Restart a service: docker-compose restart [service-name]" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "1. In Grafana, add Prometheus as a data source (URL: http://prometheus:9090)" -ForegroundColor Yellow
Write-Host "2. Import dashboards for PostgreSQL (ID 9628), Redis (ID 763), and Node Exporter (ID 1860)" -ForegroundColor Yellow
Write-Host "3. Configure your .NET application with the Ghost SDK to connect to these services" -ForegroundColor Yellow
Write-Host ""