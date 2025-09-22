using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NugetLicenseFetcher;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var pathArgument = new Argument<string>(
            name: "path",
            description: "Path to a .NET project (.csproj) or solution (.sln) file");

        var includeTransitiveOption = new Option<bool>(
            name: "--include-transitive",
            description: "Include transitive dependencies in the analysis");

        var rootCommand = new RootCommand("NuGet License Fetcher - Extract license information from NuGet packages")
        {
            pathArgument,
            includeTransitiveOption
        };

        rootCommand.SetHandler(async (path, includeTransitive) =>
        {
            await ProcessProject(path, includeTransitive);
        }, pathArgument, includeTransitiveOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task ProcessProject(string path, bool includeTransitive)
    {
        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"Error: File '{path}' not found.");
                return;
            }

            Console.WriteLine($"Processing: {path}");
            Console.WriteLine($"Include transitive dependencies: {includeTransitive}");

            var packages = await ExtractPackageReferences(path, includeTransitive);
            List<PackageDetails> notFoundPkgs = [];
            var packageDetails = await FetchPackageDetails(packages, notFoundPkgs);
            await WriteJsonOutput(packageDetails, "package-licenses.json");
            await WriteJsonOutput(notFoundPkgs, "not-found-licenses.json");

            Console.WriteLine($"Successfully processed {packageDetails.Count} packages.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static async Task<List<PackageReference>> ExtractPackageReferences(string projectPath, bool includeTransitive)
    {
        var packages = new List<PackageReference>();

        try
        {
            if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                // Handle solution files
                var solutionDir = Path.GetDirectoryName(projectPath)!;
                var solutionContent = await File.ReadAllTextAsync(projectPath);
                
                var projectFiles = ExtractProjectFilesFromSolution(solutionContent, solutionDir);
                
                foreach (var projectFile in projectFiles)
                {
                    var projectPackages = await ExtractFromProject(projectFile, includeTransitive);
                    packages.AddRange(projectPackages);
                }
            }
            else if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                // Handle single project file
                packages = await ExtractFromProject(projectPath, includeTransitive);
            }
            else
            {
                Console.WriteLine("Error: Only .csproj and .sln files are supported.");
                return packages;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading project file: {ex.Message}");
        }

        return packages.Distinct().ToList();
    }

    private static async Task<List<PackageReference>> ExtractFromProject(string projectPath, bool includeTransitive)
    {
        var packages = new List<PackageReference>();
        
        // First, get direct package references from the csproj file
        var directPackages = await ExtractFromCsproj(projectPath);
        packages.AddRange(directPackages);
        
        if (includeTransitive)
        {
            try
            {
                // Use dotnet list package command to get transitive dependencies
                var transitivePackages = await ExtractTransitiveDependencies(projectPath);
                packages.AddRange(transitivePackages);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not extract transitive dependencies: {ex.Message}");
            }
        }
        
        return packages;
    }
    
    private static async Task<List<PackageReference>> ExtractTransitiveDependencies(string projectPath)
    {
        var packages = new List<PackageReference>();
        
        try
        {
            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"list \"{projectPath}\" package --include-transitive",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath)
            };

            using var process = System.Diagnostics.Process.Start(processInfo);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    packages = ParseDotnetListPackageOutput(output);
                }
                else
                {
                    Console.WriteLine($"Error running dotnet list package: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting transitive dependencies: {ex.Message}");
        }
        
        return packages;
    }
    
    private static List<PackageReference> ParseDotnetListPackageOutput(string output)
    {
        var packages = new List<PackageReference>();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        bool inTransitiveSection = false;
        bool inTopLevelSection = false;
        
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            if (trimmedLine.Contains("Top-level Package"))
            {
                inTopLevelSection = true;
                inTransitiveSection = false;
                continue;
            }
            
            if (trimmedLine.Contains("Transitive Package"))
            {
                inTransitiveSection = true;
                inTopLevelSection = false;
                continue;
            }
            
            // Parse package lines that start with ">"
            if (trimmedLine.StartsWith(">") && (inTransitiveSection || inTopLevelSection))
            {
                var parts = trimmedLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var packageId = parts[1];
                    var version = parts[2];
                    
                    // Only add transitive packages if we're in the transitive section
                    // or direct packages if we're in the top-level section
                    if (inTransitiveSection)
                    {
                        packages.Add(new PackageReference(packageId, version, true));
                    }
                    else if (inTopLevelSection)
                    {
                        // We already have direct packages from XML parsing, so skip these
                        continue;
                    }
                }
            }
        }
        
        return packages;
    }

    private static List<string> ExtractProjectFilesFromSolution(string solutionContent, string solutionDir)
    {
        var projectFiles = new List<string>();
        var lines = solutionContent.Split('\n');
        
        foreach (var line in lines)
        {
            if (line.StartsWith("Project("))
            {
                var parts = line.Split('"');
                if (parts.Length >= 6)
                {
                    var relativePath = parts[5];
                    if (relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        var fullPath = Path.Combine(solutionDir, relativePath.Replace('\\', Path.DirectorySeparatorChar));
                        if (File.Exists(fullPath))
                        {
                            projectFiles.Add(fullPath);
                        }
                    }
                }
            }
        }
        
        return projectFiles;
    }

    private static async Task<List<PackageReference>> ExtractFromCsproj(string projectPath)
    {
        var packages = new List<PackageReference>();
        
        try
        {
            var content = await File.ReadAllTextAsync(projectPath);
            var doc = XDocument.Parse(content);
            
            var packageReferences = doc.Descendants("PackageReference");
            
            foreach (var element in packageReferences)
            {
                var include = element.Attribute("Include")?.Value;
                var version = element.Attribute("Version")?.Value ?? element.Element("Version")?.Value;
                
                if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version))
                {
                    packages.Add(new PackageReference(include, version, false));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing project file {projectPath}: {ex.Message}");
        }
        
        return packages;
    }

    private static async Task<List<PackageDetails>> FetchPackageDetails(
        List<PackageReference> packages, 
        List<PackageDetails>? notFoundPkgs = null)
    {
        var packageDetails = new List<PackageDetails>();
        var logger = NullLogger.Instance;
        var cache = new SourceCacheContext();

        using var httpClient = new HttpClient();
        var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>();

        var peergroupRegex = new Regex(@"(PSI.|EIB.|PSIS.|PTO.|PTOS.|PEERGroup.).*");

        foreach (var package in packages)
        {
            if (peergroupRegex.IsMatch(package.Id))
                continue;
            
            try
            {
                Console.WriteLine($"Fetching details for {package.Id} {package.Version}...");

                var metadata = await metadataResource.GetMetadataAsync(
                    package.Id,
                    includePrerelease: true,
                    includeUnlisted: false,
                    cache,
                    logger,
                    CancellationToken.None);

                var packageVersion = NuGetVersion.Parse(package.Version);
                var packageMetadata = metadata.FirstOrDefault(m => m.Identity.Version == packageVersion);

                if (packageMetadata != null)
                {
                    var details = new PackageDetails
                    {
                        LibName = package.Id,
                        Version = package.Version,
                        IsTransitive = package.IsTransitive,
                        SpdxIdentifier = ExtractSpdxIdentifier(packageMetadata),
                        CopyrightYear = ExtractCopyrightYear(packageMetadata.Published?.Year.ToString()),
                        LicenseUrl = packageMetadata.LicenseUrl?.ToString(),
                        ProjectUrl = packageMetadata.ProjectUrl?.ToString(),
                        Authors = packageMetadata.Authors?.Split(',').Select(a => a.Trim()).ToList() ?? new List<string>()
                    };

                    packageDetails.Add(details);
                }
                else
                {
                    var details = new PackageDetails
                    {
                        LibName = package.Id,
                        Version = package.Version,
                        IsTransitive = package.IsTransitive,
                        SpdxIdentifier = "metadata not found",
                        CopyrightYear = "metadata not found",
                        LicenseUrl = "metadata not found",
                        ProjectUrl = "metadata not found",
                        Authors = ["metadata not found", ],
                    };
                    notFoundPkgs?.Add(details);
                    throw new MetadataNotFoundException($"metadata not found.");
                }
                
                Console.WriteLine($"Successfully fetched details for {package.Id} {package.Version}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching details for {package.Id}: {ex.Message}");
            }
        }

        return packageDetails;
    }

    private static string? ExtractCopyrightYear(string? year)
    {
        return year; // Return the year as-is if provided
    }

    private static string? ExtractSpdxIdentifier(IPackageSearchMetadata packageMetadata)
    {
        try
        {
            // Use reflection to access LicenseExpression property since it's not on the interface
            var type = packageMetadata.GetType();
            var licenseExpressionProp = type.GetProperty("LicenseExpression");
            if (licenseExpressionProp != null)
            {
                var licenseExpression = licenseExpressionProp.GetValue(packageMetadata) as string;
                return string.IsNullOrWhiteSpace(licenseExpression) ? null : licenseExpression;
            }
        }
        catch (Exception)
        {
            // If we can't access LicenseExpression, return null
        }
        
        return null;
    }

    private static async Task WriteJsonOutput(List<PackageDetails> packageDetails, string outputPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(packageDetails, options);
        await File.WriteAllTextAsync(outputPath, json);
        
        Console.WriteLine($"Output written to: {outputPath}");
    }
}

public record PackageReference(string Id, string Version, bool IsTransitive)
{
    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Version);
    }

    public virtual bool Equals(PackageReference? other)
    {
        return other != null && Id == other.Id && Version == other.Version;
    }
}

public class PackageDetails
{
    public string LibName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsTransitive { get; set; }
    public string? SpdxIdentifier { get; set; }
    public string? CopyrightYear { get; set; }
    public string? LicenseUrl { get; set; }
    public string? ProjectUrl { get; set; }
    public List<string> Authors { get; set; } = new();
}
