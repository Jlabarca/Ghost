# GhostFather 👻

A modern .NET-based application orchestration framework for building, deploying, and managing distributed applications with ease.

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)
![Stage](https://img.shields.io/badge/stage-alpha-orange.svg)

## 🌟 Features

- **Process Orchestration**: Sophisticated process management and supervision with automatic recovery
- **Dual-Mode Operation**: Run locally with SQLite/in-memory or distributed with PostgreSQL/Redis
- **Health Monitoring**: Built-in health checks, metrics collection, and process monitoring
- **Configuration Management**: Hot-reloadable YAML configuration system
- **Template System**: Bootstrap new applications quickly with customizable templates
- **CLI Interface**: Powerful command-line tools for application management
- **Structured Logging**: Comprehensive logging with file rotation and Redis integration
- **Distributed Communication**: Built-in message bus pattern for inter-process communication

## 🏗️ Architecture

```mermaid
graph TB
    subgraph CLI["CLI Layer" style="fill:#353535"]
        CLI1["Ghost CLI"]
        CLI2["Commands"]
        CLI3["Templates"]
    end

    subgraph SDK["SDK Layer" style="fill:#3c6e71"]
        SDK1["GhostApp"]
        SDK2["Lifecycle Hooks"]
        SDK3["Service Mode"]
    end

    subgraph Core["Core Services" style="fill:#284b63"]
        C1["Message Bus"]
        C2["Data Access"]
        C3["Config"]
        C4["Metrics"]
    end

    subgraph Storage["Storage Layer" style="fill:#353535"]
        S1["SQLite/PostgreSQL"]
        S2["Redis/Memory Cache"]
    end

    subgraph Process["Process Management" style="fill:#3c6e71"]
        P1["GhostFather"]
        P2["Health Monitor"]
        P3["State Manager"]
    end

    CLI1 --> SDK1
    SDK1 --> SDK2
    SDK1 --> SDK3
    SDK1 --> C1
    SDK1 --> C2
    SDK1 --> C3
    SDK1 --> C4
    C1 --> S2
    C2 --> S1
    C3 --> S2
    P1 --> C1
    P1 --> P2
    P1 --> P3
    P2 --> C4
    P3 --> S1
```

## 🚀 Quick Start

1. Install the Ghost CLI:
```bash
dotnet tool install --global Ghost
```

2. Create a new Ghost application:
```bash
ghost create MyApp
```

3. Run your application:
```bash
ghost run MyApp
```

## 💻 Development Setup

Requirements:
- .NET 8.0 SDK
- Git
- Redis (optional, for distributed mode)
- PostgreSQL (optional, for distributed mode)

Build and install locally:
```bash
# Clone repository
git clone https://github.com/yourusername/ghost.git
cd ghost

# Build project
dotnet build

# Create NuGet package
dotnet pack

# Install locally
dotnet tool install --global --add-source ./nupkg Ghost
```

## 🛠️ Configuration

Ghost uses YAML for configuration. Create a `.ghost.yaml` in your project root:

```yaml
system:
  id: my-app
  mode: local  # or 'distributed'

storage:
  redis:
    enabled: false
    connection: "localhost:6379"
  postgres:
    enabled: false
    connection: "Host=localhost;Database=ghost;"

monitoring:
  enabled: true
  interval: "00:00:05"
```

## 📊 Process Management

GhostFather provides sophisticated process management:

- Automatic process recovery
- Health monitoring
- Resource usage tracking
- State persistence
- Inter-process communication

Monitor your processes:
```bash
ghost monitor MyApp
```

## 🔌 SDK Usage

Create a simple one-off Ghost application:

```csharp
public class MyApp : GhostApp
{
    public override async Task RunAsync()
    {
        // Your application logic here
        await Task.Delay(1000);
        Ghost.LogInfo("Hello from MyApp!");
    }
}
```

Create a long-running Ghost service:

```csharp
public class MyService : GhostApp
{
    public MyService()
    {
        // Configure as service
        IsService = true;
        TickInterval = TimeSpan.FromSeconds(1);
        AutoRestart = true;
    }

    public override async Task RunAsync()
    {
        // Initial setup
        Ghost.LogInfo("Service starting...");
    }

    protected override async Task OnTickAsync()
    {
        // Regular service work
        Ghost.LogInfo("Service heartbeat");
        await ProcessWorkItems();
    }

    protected override async Task OnAfterRunAsync()
    {
        // Cleanup
        Ghost.LogInfo("Service shutting down...");
    }
}
```

## 📦 Templates

Ghost includes built-in templates for common application patterns:

- Basic console application
- Long-running service
- Web API
- Worker service

Create from template:
```bash
ghost create MyApp --template service
```

## 📝 Logging

Ghost provides structured logging with multiple outputs:

- Console logging
- File logging with rotation
- Redis logging (distributed mode)
- Metrics collection

## 🤝 Contributing

We welcome contributions! Please read our [Contributing Guidelines](CONTRIBUTING.md) before submitting PRs.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.