using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Adyen.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using System.Collections.Generic;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.OpenApi.Merger.Abstract;
using System.Text.RegularExpressions;

namespace Soenneker.Adyen.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private static readonly Regex _versionedJsonRegex = new(@"^(?<name>.+)-v(?<version>\d+)\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IOpenApiMerger _openApiMerger;
    private readonly IOpenApiFixer _openApiFixer;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IFileUtil fileUtil, IDirectoryUtil directoryUtil, IOpenApiMerger openApiMerger, IOpenApiFixer openApiFixer, IKiotaUtil kiotaUtil)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _openApiMerger = openApiMerger;
        _openApiFixer = openApiFixer;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string targetFilePath = Path.Combine(gitDirectory, "openapi.json");

        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string openApiGitUrl = _configuration["Adyen:ClientGenerationUrl"] ?? "https://github.com/Adyen/adyen-openapi";

        string sourceDirectory = await _gitUtil.CloneToTempDirectory(NormalizeGitRepositoryUrl(openApiGitUrl), cancellationToken: cancellationToken);
        string latestJsonDirectory = await CopyLatestJsonFiles(sourceDirectory, cancellationToken);

        OpenApiDocument merged = await _openApiMerger.MergeDirectory(latestJsonDirectory, cancellationToken);
        string json = _openApiMerger.ToJson(merged);

        await _fileUtil.Write(targetFilePath, json, true, cancellationToken);

        string fixedFilePath = Path.Combine(gitDirectory, "fixed.json");

        await _openApiFixer.Fix(targetFilePath, fixedFilePath, cancellationToken);

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "AdyenOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    private async ValueTask<string> CopyLatestJsonFiles(string sourceDirectory, CancellationToken cancellationToken)
    {
        string jsonSourceDirectory = Path.Combine(sourceDirectory, "json");

        if (!Directory.Exists(jsonSourceDirectory))
            throw new DirectoryNotFoundException($"Adyen JSON directory was not found: '{jsonSourceDirectory}'.");

        string targetDirectory = Path.Combine(Path.GetTempPath(), $"adyen-openapi-json-{Guid.NewGuid():N}");
        await _directoryUtil.Create(targetDirectory, false, cancellationToken);

        string[] jsonFiles = Directory.GetFiles(jsonSourceDirectory, "*.json", SearchOption.TopDirectoryOnly);

        var latestFiles = jsonFiles
            .Select(filePath => new
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Match = _versionedJsonRegex.Match(Path.GetFileName(filePath))
            })
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => int.Parse(x.Match.Groups["version"].Value)).First())
            .ToList();

        foreach (var latestFile in latestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string destinationPath = Path.Combine(targetDirectory, latestFile.FileName);
            File.Copy(latestFile.FilePath, destinationPath, overwrite: true);
        }

        _logger.LogInformation("Selected {Count} latest versioned Adyen JSON files from {SourceDirectory}", latestFiles.Count, jsonSourceDirectory);

        return targetDirectory;
    }

    private static string NormalizeGitRepositoryUrl(string url)
    {
        const string treeSegment = "/tree/";
        int treeIndex = url.IndexOf(treeSegment, StringComparison.OrdinalIgnoreCase);

        return treeIndex > -1 ? url[..treeIndex] : url;
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
