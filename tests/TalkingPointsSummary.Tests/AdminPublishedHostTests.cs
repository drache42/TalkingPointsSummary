using System.Diagnostics;
using System.Net;
using System.Net.Http;
using FluentAssertions;

namespace TalkingPointsSummary.Tests;

public class AdminPublishedHostTests
{
    private static readonly SemaphoreSlim PublishLock = new(1, 1);
    private static string? _publishedTemplatePath;

    [Fact]
    public async Task PublishedAdminApp_ExitsWhenDatabaseIsUnavailable()
    {
        var publishPath = await CreatePublishedCopyAsync();
        var port = GetFreeTcpPort();

        using var app = StartAdminProcess(
            publishPath,
            port,
            "Host=127.0.0.1;Port=1;Database=talkingpoints;Username=postgres;Password=postgres;Timeout=1;Command Timeout=1");

        var exited = await WaitForExitAsync(app.Process, TimeSpan.FromSeconds(15));

        exited.Should().BeTrue("the admin app should fail fast when the database is unavailable during startup");
        app.Process.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task PublishedAdminApp_ServesBlazorFrameworkAssetWithoutPhysicalFrameworkFiles()
    {
        var publishPath = await CreatePublishedCopyAsync();
        var frameworkPath = Path.Combine(publishPath, "wwwroot", "_framework");
        if (Directory.Exists(frameworkPath))
        {
            Directory.Delete(frameworkPath, recursive: true);
        }

        var port = GetFreeTcpPort();
        var postgresPort = GetFreeTcpPort();

        using var postgres = await StartPostgresContainerAsync(postgresPort);

        using var app = StartAdminProcess(
            publishPath,
            port,
            $"Host=127.0.0.1;Port={postgresPort};Database=talkingpoints;Username=postgres;Password=postgres");

        using var httpClient = new HttpClient();
        var response = await WaitForSuccessfulResponseAsync(
            httpClient,
            new Uri($"http://127.0.0.1:{port}/_framework/blazor.web.js"),
            app.Process,
            TimeSpan.FromSeconds(15));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");
    }

    private static async Task<string> CreatePublishedCopyAsync()
    {
        await PublishLock.WaitAsync();

        try
        {
            _publishedTemplatePath ??= await PublishAdminAppAsync();
        }
        finally
        {
            PublishLock.Release();
        }

        var copyPath = Path.Combine(Path.GetTempPath(), "tps-admin-publish-copy-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(_publishedTemplatePath, copyPath);
        return copyPath;
    }

    private static async Task<string> PublishAdminAppAsync()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "tps-admin-publish-template-" + Guid.NewGuid().ToString("N"));
        var repoRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repoRoot, "src", "TalkingPointsSummary.Admin", "TalkingPointsSummary.Admin.csproj");

        using var publish = CreateProcess(
            "dotnet",
            $"publish \"{projectPath}\" -c Release -o \"{outputPath}\"",
            repoRoot);

        publish.Start();
        var publishOutput = await publish.StandardOutput.ReadToEndAsync();
        var publishError = await publish.StandardError.ReadToEndAsync();
        await publish.WaitForExitAsync();

        if (publish.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed with exit code {publish.ExitCode}.{Environment.NewLine}{publishOutput}{Environment.NewLine}{publishError}");
        }

        return outputPath;
    }

    private static RunningAdminProcess StartAdminProcess(string publishPath, int port, string connectionString)
    {
        var process = CreateProcess(
            "dotnet",
            "TalkingPointsSummary.Admin.dll",
            publishPath);

        process.StartInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        process.StartInfo.Environment["ConnectionStrings__TalkingPoints"] = connectionString;

        process.Start();
        return new RunningAdminProcess(process, publishPath);
    }

    private static async Task<HttpResponseMessage> WaitForSuccessfulResponseAsync(
        HttpClient httpClient,
        Uri requestUri,
        Process process,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HttpResponseMessage? lastResponse = null;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Admin process exited before serving {requestUri}. Exit code: {process.ExitCode}");
            }

            try
            {
                var response = await httpClient.GetAsync(requestUri);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                lastResponse?.Dispose();
                lastResponse = response;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250);
        }

        if (lastResponse is not null)
        {
            throw new InvalidOperationException($"Timed out waiting for a successful response from {requestUri}. Last status code: {(int)lastResponse.StatusCode}");
        }

        throw new InvalidOperationException($"Timed out waiting for {requestUri}.");
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellationTokenSource = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(cancellationTokenSource.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private static Process CreateProcess(string fileName, string arguments, string workingDirectory)
        => new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

    private static async Task<RunningDockerContainer> StartPostgresContainerAsync(int hostPort)
    {
        var containerName = "tps-admin-test-postgres-" + Guid.NewGuid().ToString("N");
        var runArguments = string.Join(' ',
            "run --rm -d",
            $"--name {containerName}",
            $"-p 127.0.0.1:{hostPort}:5432",
            "-e POSTGRES_DB=talkingpoints",
            "-e POSTGRES_USER=postgres",
            "-e POSTGRES_PASSWORD=postgres",
            "postgres:15-alpine");

        await RunProcessAsync("docker", runArguments, FindRepositoryRoot());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await RunProcessAsync(
                    "docker",
                    $"exec {containerName} pg_isready -U postgres -d talkingpoints",
                    FindRepositoryRoot());

                return new RunningDockerContainer(containerName);
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(500);
            }
        }

        throw new InvalidOperationException($"Timed out waiting for postgres container {containerName} to become ready.");
    }

    private static async Task RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        using var process = CreateProcess(fileName, arguments, workingDirectory);
        process.Start();
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Process '{fileName} {arguments}' failed with exit code {process.ExitCode}.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "TalkingPointsSummary.sln");
            if (File.Exists(solutionPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var directoryPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath, StringComparison.Ordinal));
        }

        foreach (var filePath in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var destinationFilePath = filePath.Replace(sourcePath, destinationPath, StringComparison.Ordinal);
            File.Copy(filePath, destinationFilePath, overwrite: true);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class RunningAdminProcess(Process process, string publishPath) : IDisposable
    {
        public Process Process { get; } = process;

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit();
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                if (Directory.Exists(publishPath))
                {
                    Directory.Delete(publishPath, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Process.Dispose();
        }
    }

    private sealed class RunningDockerContainer(string containerName) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                using var process = CreateProcess("docker", $"rm -f {containerName}", FindRepositoryRoot());
                process.Start();
                process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}