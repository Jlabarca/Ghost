# Ghost CLI

A modern .NET CLI application with full CI/CD setup.

## Installation

You can install Ghost CLI as a global .NET tool:

```bash
dotnet tool install --global Ghost
```

## Usage

Create a new project:
```bash
ghost create MyApp
```

Run a project from a repository:
```bash
ghost run --url https://github.com/user/app
```

Create an alias for a repository:
```bash
ghost alias --create myapp --url https://github.com/user/app
```

Run a project using an alias:
```bash
ghost run myapp
```

Push your project to GitHub:
```bash
ghost push MyApp --token your-github-token
```

## Development Requirements

- .NET 8.0 SDK
- Git

## Build and Package

```bash
# Build the project
dotnet build

# Create NuGet package
dotnet pack

# Install locally for testing
dotnet tool install --global --add-source ./nupkg Ghost
```

## License

MIT