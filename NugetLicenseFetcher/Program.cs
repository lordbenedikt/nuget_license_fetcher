using System.CommandLine;
using System.Text.Json;
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
            var packageDetails = await FetchPackageDetails(packages);
            await WriteJsonOutput(packageDetails);

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
                    var projectPackages = await ExtractFromCsproj(projectFile);
                    packages.AddRange(projectPackages);
                }
            }
            else if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                // Handle single project file
                packages = await ExtractFromCsproj(projectPath);
            }
            else
            {
                Console.WriteLine("Error: Only .csproj and .sln files are supported.");
                return packages;
            }

            // TODO: Add transitive dependency extraction if includeTransitive is true
            if (includeTransitive)
            {
                Console.WriteLine("Note: Transitive dependency extraction not yet implemented.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading project file: {ex.Message}");
        }

        return packages.Distinct().ToList();
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

    private static async Task<List<PackageDetails>> FetchPackageDetails(List<PackageReference> packages)
    {
        var packageDetails = new List<PackageDetails>();
        var logger = NullLogger.Instance;
        var cache = new SourceCacheContext();

        using var httpClient = new HttpClient();
        var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>();

        foreach (var package in packages)
        {
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
                        CopyrightOwner = ExtractCopyrightOwner(packageMetadata.Owners),
                        CopyrightYear = ExtractCopyrightYear(packageMetadata.Published?.Year.ToString()),
                        LicenseUrl = packageMetadata.LicenseUrl?.ToString(),
                        ProjectUrl = packageMetadata.ProjectUrl?.ToString(),
                        Authors = packageMetadata.Authors?.Split(',').Select(a => a.Trim()).ToList() ?? new List<string>()
                    };

                    packageDetails.Add(details);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching details for {package.Id}: {ex.Message}");
            }
        }

        return packageDetails;
    }

    private static string? ExtractCopyrightOwner(string? owners)
    {
        if (string.IsNullOrEmpty(owners))
            return null;

        // For owners, just return the first owner or the whole string if short
        var ownerList = owners.Split(',').Select(o => o.Trim()).Where(o => !string.IsNullOrEmpty(o));
        return ownerList.FirstOrDefault();
    }

    private static string? ExtractCopyrightYear(string? year)
    {
        return year; // Return the year as-is if provided
    }

    private static async Task WriteJsonOutput(List<PackageDetails> packageDetails)
    {
        var outputPath = "package-licenses.json";
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
    public string? CopyrightOwner { get; set; }
    public string? CopyrightYear { get; set; }
    public string? LicenseUrl { get; set; }
    public string? ProjectUrl { get; set; }
    public List<string> Authors { get; set; } = new();
}
