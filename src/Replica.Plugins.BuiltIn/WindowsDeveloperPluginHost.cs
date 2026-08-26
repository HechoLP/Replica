using System.Diagnostics;
using System.Text;
using Replica.Core.Plugins;

namespace Replica.Plugins.BuiltIn;

public sealed class WindowsDeveloperPluginHost : IDeveloperPluginHost
{
    private const int MaximumFileCharacters = 1_048_576;
    private const int MaximumOutputCharacters = 1_048_576;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    public string GetKnownPath(DeveloperKnownPath path)
    {
        return path switch
        {
            DeveloperKnownPath.UserProfile => Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            DeveloperKnownPath.RoamingApplicationData => Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            DeveloperKnownPath.LocalApplicationData => Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            DeveloperKnownPath.Documents => Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments),
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
        };
    }

    public bool FileExists(string path)
    {
        return IsSafeFile(path);
    }

    public bool DirectoryExists(string path)
    {
        return IsSafeDirectory(path);
    }

    public long? GetFileSize(string path)
    {
        return IsSafeFile(path) ? new FileInfo(path).Length : null;
    }

    public async Task<string?> ReadTextFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!IsSafeFile(path))
        {
            return null;
        }

        FileInfo file = new(path);
        if (file.Length > MaximumFileCharacters * 4L)
        {
            return null;
        }

        await using FileStream stream = new(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        char[] buffer = new char[4096];
        StringBuilder text = new(Math.Min((int)Math.Min(file.Length, 16_384), MaximumFileCharacters));
        while (text.Length < MaximumFileCharacters)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, MaximumFileCharacters - text.Length)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    public IReadOnlyList<string> EnumerateFiles(
        string path,
        string searchPattern,
        bool recursive)
    {
        if (!IsSafeDirectory(path) ||
            string.IsNullOrWhiteSpace(searchPattern) ||
            searchPattern.Contains(Path.DirectorySeparatorChar) ||
            searchPattern.Contains(Path.AltDirectorySeparatorChar))
        {
            return [];
        }

        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(Path.GetFullPath(path));
        while (pending.Count > 0 && files.Count < 2_000)
        {
            string current = pending.Pop();
            try
            {
                files.AddRange(Directory
                    .EnumerateFiles(current, searchPattern, SearchOption.TopDirectoryOnly)
                    .Where(IsSafeFile)
                    .Take(2_000 - files.Count));
                if (recursive)
                {
                    foreach (string directory in Directory
                                 .EnumerateDirectories(current)
                                 .Where(IsSafeDirectory)
                                 .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(directory);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A partial, read-only inventory is preferable to following an unsafe path.
            }
        }

        return files.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public BuiltInApplicationInfo GetApplicationInfo(BuiltInApplication application)
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string localAppData = GetKnownPath(DeveloperKnownPath.LocalApplicationData);
        string roamingAppData = GetKnownPath(DeveloperKnownPath.RoamingApplicationData);
        string? executable = application switch
        {
            BuiltInApplication.PowerToys => FirstSafeFile(
                Path.Combine(localAppData, "PowerToys", "PowerToys.exe"),
                Path.Combine(programFiles, "PowerToys", "PowerToys.exe")),
            BuiltInApplication.Everything => FirstSafeFile(
                Path.Combine(programFiles, "Everything", "Everything.exe"),
                Path.Combine(localAppData, "Everything", "Everything.exe")),
            BuiltInApplication.ObsStudio => FirstSafeFile(
                Path.Combine(programFiles, "obs-studio", "bin", "64bit", "obs64.exe")),
            BuiltInApplication.Minecraft => FirstSafeFile(
                Path.Combine(programFiles, "Minecraft Launcher", "MinecraftLauncher.exe"),
                Path.Combine(
                    localAppData,
                    "Microsoft",
                    "WindowsApps",
                    "Minecraft.exe")),
            BuiltInApplication.DockerDesktop => FirstSafeFile(
                Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe")),
            BuiltInApplication.AbletonLive => FindAbletonExecutable(programFiles),
            _ => throw new ArgumentOutOfRangeException(nameof(application), application, null),
        };
        bool dataOnlyMinecraft = application == BuiltInApplication.Minecraft &&
            IsSafeDirectory(Path.Combine(roamingAppData, ".minecraft"));
        bool installed = executable is not null || dataOnlyMinecraft;
        string? version = executable is null ? null : TryGetFileVersion(executable);
        return new BuiltInApplicationInfo(
            installed,
            version,
            IsApplicationRunning(application),
            executable);
    }

    public async Task<DeveloperToolQueryResult> QueryAsync(
        DeveloperToolQuery query,
        CancellationToken cancellationToken)
    {
        (string executable, string[] arguments) = GetCommand(query, resolvedPythonLauncher: null);
        string? resolved = ResolveExecutable(executable);
        if (resolved is null)
        {
            return new DeveloperToolQueryResult(false, string.Empty);
        }

        if (query == DeveloperToolQuery.PythonPackages)
        {
            string? pythonLauncher = ResolveExecutable("py.exe");
            if (pythonLauncher is null)
            {
                return new DeveloperToolQueryResult(false, string.Empty);
            }

            (_, arguments) = GetCommand(query, pythonLauncher);
        }

        ProcessStartInfo startInfo = new(resolved)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new DeveloperToolQueryResult(false, string.Empty);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new DeveloperToolQueryResult(false, string.Empty);
        }

        Task<string> outputTask = ReadBoundedAsync(process.StandardOutput);
        Task<string> errorTask = ReadBoundedAsync(process.StandardError);
        using CancellationTokenSource timeout = new(QueryTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            return new DeveloperToolQueryResult(
                true,
                string.Empty,
                Warnings: ["The developer tool query timed out."]);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            throw;
        }

        string output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        return new DeveloperToolQueryResult(
            true,
            process.ExitCode == 0 ? output : string.Empty,
            FirstNonEmptyLine(output),
            process.ExitCode == 0 ? [] : ["The developer tool query did not complete successfully."]);
    }

    private static (string Executable, string[] Arguments) GetCommand(
        DeveloperToolQuery query,
        string? resolvedPythonLauncher)
    {
        return query switch
        {
            DeveloperToolQuery.VisualStudioCodeVersion => ("code.cmd", ["--version"]),
            DeveloperToolQuery.VisualStudioCodeExtensions =>
                ("code.cmd", ["--list-extensions", "--show-versions"]),
            DeveloperToolQuery.GitVersion => ("git.exe", ["--version"]),
            DeveloperToolQuery.GitConfiguration =>
                ("git.exe", ["config", "--global", "--get-regexp", "^(user\\.(name|email)|init\\.defaultBranch|core\\.editor|credential\\.helper|alias\\..+)$"]),
            DeveloperToolQuery.PowerShellVersion =>
                ("pwsh.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]),
            DeveloperToolQuery.PowerShellModules =>
                ("pwsh.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Get-Module -ListAvailable | Select-Object Name,Version | ConvertTo-Json -Compress"]),
            DeveloperToolQuery.NodeVersion => ("node.exe", ["--version"]),
            DeveloperToolQuery.NpmVersion => ("npm.cmd", ["--version"]),
            DeveloperToolQuery.NpmGlobalPackages =>
                ("npm.cmd", ["list", "--global", "--depth=0", "--json"]),
            DeveloperToolQuery.PythonInterpreters => ("py.exe", ["-0p"]),
            DeveloperToolQuery.PythonPackages =>
                ("pwsh.exe", [
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    BuildPythonPackageQuery(resolvedPythonLauncher),
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query, null),
        };
    }

    private static string BuildPythonPackageQuery(string? resolvedPythonLauncher)
    {
        if (string.IsNullOrWhiteSpace(resolvedPythonLauncher))
        {
            return "throw 'The trusted Python launcher path is unavailable.'";
        }

        string quotedLauncher = resolvedPythonLauncher.Replace("'", "''", StringComparison.Ordinal);
        return $"$items = @(); & '{quotedLauncher}' -0p | ForEach-Object {{ if ($_ -match '([A-Za-z]:[\\/].*python(?:\\.exe)?)\\s*$') {{ $path = $Matches[1]; if (Test-Path -LiteralPath $path -PathType Leaf) {{ $packages = & $path -m pip list --format=json 2>$null; if ($LASTEXITCODE -eq 0) {{ $items += [pscustomobject]@{{ Interpreter = $path; Packages = ($packages | ConvertFrom-Json) }} }} }} }} }}; $items | ConvertTo-Json -Compress -Depth 5";
    }

    private static string? ResolveExecutable(string executable)
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] candidates = executable.ToLowerInvariant() switch
        {
            "code.cmd" =>
            [
                Path.Combine(programFiles, "Microsoft VS Code", "bin", "code.cmd"),
                Path.Combine(programFilesX86, "Microsoft VS Code", "bin", "code.cmd"),
            ],
            "git.exe" =>
            [
                Path.Combine(programFiles, "Git", "cmd", "git.exe"),
                Path.Combine(programFiles, "Git", "bin", "git.exe"),
                Path.Combine(programFilesX86, "Git", "cmd", "git.exe"),
            ],
            "pwsh.exe" =>
            [
                Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
                Path.Combine(
                    windows,
                    "System32",
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
            ],
            "node.exe" => [Path.Combine(programFiles, "nodejs", "node.exe")],
            "npm.cmd" => [Path.Combine(programFiles, "nodejs", "npm.cmd")],
            "py.exe" =>
            [
                Path.Combine(windows, "py.exe"),
                Path.Combine(programFiles, "Python Launcher", "py.exe"),
            ],
            _ => [],
        };

        return candidates
            .Where(Path.IsPathFullyQualified)
            .FirstOrDefault(IsSafeFile);
    }

    private static string? FirstSafeFile(params string[] candidates)
    {
        return candidates.FirstOrDefault(IsSafeFile);
    }

    private static string? FindAbletonExecutable(string programFiles)
    {
        string root = Path.Combine(programFiles, "Ableton");
        if (!IsSafeDirectory(root))
        {
            return null;
        }

        try
        {
            foreach (string directory in Directory
                         .EnumerateDirectories(root, "Ableton Live *", SearchOption.TopDirectoryOnly)
                         .Where(IsSafeDirectory)
                         .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string? executable = Directory
                    .EnumerateFiles(
                        Path.Combine(directory, "Program"),
                        "Ableton Live*.exe",
                        SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(IsSafeFile);
                if (executable is not null)
                {
                    return executable;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Application detection is read-only and reports the executable as unavailable.
        }

        return null;
    }

    private static string? TryGetFileVersion(string path)
    {
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            return version.ProductVersion ?? version.FileVersion;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsApplicationRunning(BuiltInApplication application)
    {
        string[] names = application switch
        {
            BuiltInApplication.PowerToys => ["PowerToys"],
            BuiltInApplication.Everything => ["Everything"],
            BuiltInApplication.ObsStudio => ["obs64", "obs32"],
            BuiltInApplication.Minecraft => ["Minecraft", "MinecraftLauncher"],
            BuiltInApplication.DockerDesktop => ["Docker Desktop"],
            BuiltInApplication.AbletonLive => ["Ableton Live"],
            _ => throw new ArgumentOutOfRangeException(nameof(application), application, null),
        };

        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (names.Any(name => process.ProcessName.StartsWith(
                            name,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A process can exit or become inaccessible while the read-only inventory runs.
                }
            }

            return false;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        StringBuilder output = new(16_384);
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToString();
            }

            int remaining = MaximumOutputCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }

    private static string? FirstNonEmptyLine(string value)
    {
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
    }

    private static bool IsSafeFile(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return false;
            }

            FileInfo file = new(fullPath);
            return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            DirectoryInfo directory = new(Path.GetFullPath(path));
            return directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the bounded kill request.
        }
    }
}
