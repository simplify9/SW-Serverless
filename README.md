
# SW.Serverless

[![GitHub Actions](https://github.com/simplify9/SW-Serverless/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/simplify9/SW-Serverless/actions/workflows/nuget-publish.yml)
[![NuGet - SimplyWorks.Serverless](https://img.shields.io/nuget/v/SimplyWorks.Serverless.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless)
[![NuGet - SimplyWorks.Serverless.Sdk](https://img.shields.io/nuget/v/SimplyWorks.Serverless.Sdk.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless.Sdk)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**SW.Serverless** is an open-source .NET framework for building and running serverless adapters and services. It provides a runtime environment for executing .NET applications as serverless functions with process isolation and communication through stdin/stdout.

## NuGet Packages

| Package | Version | Downloads |
| ------- | ------- | --------- |
| `SimplyWorks.Serverless` | [![NuGet](https://img.shields.io/nuget/v/SimplyWorks.Serverless.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless) | [![Downloads](https://img.shields.io/nuget/dt/SimplyWorks.Serverless.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless) |
| `SimplyWorks.Serverless.Sdk` | [![NuGet](https://img.shields.io/nuget/v/SimplyWorks.Serverless.Sdk.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless.Sdk) | [![Downloads](https://img.shields.io/nuget/dt/SimplyWorks.Serverless.Sdk.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless.Sdk) |

## What's Included

- **SW.Serverless**: Core serverless service library with dependency injection extensions for ASP.NET Core
- **SW.Serverless.Sdk**: SDK for developing serverless adapters with the `Runner` class and logging utilities
- **SW.Serverless.SampleWeb**: Example ASP.NET Core web application showing integration
- **SW.Serverless.Installer**: Command-line tool for packaging and deploying adapters to cloud storage

## Quick Start

### Install NuGet Packages

```bash
dotnet add package SimplyWorks.Serverless
dotnet add package SimplyWorks.Serverless.Sdk
```

### Add to ASP.NET Core

```csharp
// In Startup.cs or Program.cs
services.AddServerless();
```

### Create an Adapter

```csharp
using SW.Serverless.Sdk;

class Handler
{
    public async Task<string> ProcessData(string input)
    {
        // Your serverless logic here
        return $"Processed: {input}";
    }
}

class Program
{
    static async Task Main(string[] args) => await Runner.Run(new Handler());
}
```

## Features

- **Process Isolation**: Each adapter runs in its own process with timeout management
- **Cloud Storage Integration**: Support for AWS S3, Azure Storage, and Oracle Cloud
- **Dependency Injection**: Built-in integration with ASP.NET Core DI container
- **Logging**: Structured logging support through `AdapterLogger`
- **Caching**: Adapter metadata caching with configurable duration
- **Version Management**: Semantic versioning support for adapter deployments

## Architecture

The framework consists of:

1. **ServerlessService**: Manages adapter lifecycle, installation, and invocation
2. **Runner**: Entry point for adapter applications with command processing
3. **Installer**: CLI tool for building and deploying adapter packages
4. **Cloud Storage**: Abstraction layer for different cloud storage providers

## Support

For issues, questions, or contributions, please visit the [GitHub repository](https://github.com/simplify9/SW-Serverless).
