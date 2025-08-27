# NuGet License Fetcher

A console application that extracts license information from NuGet packages used in .NET projects and solutions.

## Features

- Analyzes .NET project files (`.csproj`) and solution files (`.sln`)
- Extracts information from both direct and transitive NuGet package dependencies
- Fetches package metadata including license URLs, authors, and copyright information
- Outputs results in JSON format for easy processing

## Usage

```bash
NugetLicenseFetcher <path> [options]
```

### Arguments

- `<path>` - Path to a .NET project (.csproj) or solution (.sln) file

### Options

- `--include-transitive` - Include transitive dependencies in the analysis
- `--help` - Show help and usage information

### Examples

Analyze a single project (direct dependencies only):
```bash
NugetLicenseFetcher MyProject.csproj
```

Analyze a solution with transitive dependencies:
```bash
NugetLicenseFetcher MySolution.sln --include-transitive
```

## Output

The application generates a `package-licenses.json` file containing detailed information about each package:

```json
[
  {
    "libName": "Newtonsoft.Json",
    "version": "13.0.3",
    "isTransitive": false,
    "copyrightOwner": null,
    "copyrightYear": "2023",
    "licenseUrl": "https://www.nuget.org/packages/Newtonsoft.Json/13.0.3/license",
    "projectUrl": "https://www.newtonsoft.com/json",
    "authors": [
      "James Newton-King"
    ]
  }
]
```

### Output Fields

- `libName` - The NuGet package ID
- `version` - The package version used in the project
- `isTransitive` - Boolean indicating if this is a transitive dependency
- `copyrightOwner` - The package owner (currently extracted from package metadata)
- `copyrightYear` - The copyright year (extracted from package publication date)
- `licenseUrl` - URL to the package license
- `projectUrl` - URL to the package's project homepage
- `authors` - List of package authors

## Requirements

- .NET 8.0 or later
- Internet connection (for fetching package metadata from NuGet.org)

## Building

```bash
cd NugetLicenseFetcher
dotnet build
```

## Running

```bash
cd NugetLicenseFetcher
dotnet run -- <path> [options]
```

Or build and run the executable:
```bash
dotnet build -c Release
./bin/Release/net8.0/NugetLicenseFetcher <path> [options]
```