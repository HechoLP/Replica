using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

internal sealed class SnapshotSelectionPolicy
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(
        ["cache", "caches", "epic games", "log", "logs", "steamapps", "temp", "temporary internet files", "tmp", "user data"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedFileNames = new(
        [
            ".env",
            "id_rsa",
            "id_dsa",
            "id_ecdsa",
            "id_ed25519",
            "cookies",
            "credentials",
            "login data",
            "local state",
            "web data",
            "wallet.dat",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedExtensions = new(
        [".key", ".log", ".p12", ".pem", ".pfx", ".ppk", ".temp", ".tmp"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedInstallerExtensions = new(
        [".appx", ".appxbundle", ".cab", ".exe", ".msi", ".msix", ".msixbundle", ".zip"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<string> restrictedRoots;

    public SnapshotSelectionPolicy()
        : this(GetRestrictedRoots())
    {
    }

    internal SnapshotSelectionPolicy(IEnumerable<string> restrictedRoots)
    {
        this.restrictedRoots = restrictedRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void ValidateSelectedRoot(string sourcePath)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;

        if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            restrictedRoots.Any(restricted => PathsEqual(fullPath, restricted)) ||
            ExcludedDirectoryNames.Contains(Path.GetFileName(fullPath)) ||
            IsBrowserProfileRoot(fullPath))
        {
            throw new ReplicaSnapshotException("The selected folder is too broad to collect safely.");
        }
    }

    public bool ShouldExcludeDirectory(string path, out ReplicaExclusion exclusion)
    {
        if (ExcludedDirectoryNames.Contains(Path.GetFileName(path)))
        {
            exclusion = new ReplicaExclusion(path, "DefaultDirectoryExclusion");
            return true;
        }

        exclusion = null!;
        return false;
    }

    public bool ShouldExcludeFile(string path, out ReplicaExclusion exclusion)
    {
        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(path);

        if (ExcludedFileNames.Contains(fileName) ||
            fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            ExcludedExtensions.Contains(extension) ||
            fileName.Contains("recovery", StringComparison.OrdinalIgnoreCase) &&
            fileName.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            exclusion = new ReplicaExclusion(path, "SensitiveFile");
            return true;
        }

        exclusion = null!;
        return false;
    }

    public void ValidateOfflineInstaller(string path)
    {
        if (!AllowedInstallerExtensions.Contains(Path.GetExtension(path)))
        {
            throw new ReplicaSnapshotException("The selected offline installer has an unsupported file type.");
        }
    }

    private static IEnumerable<string> GetRestrictedRoots()
    {
        yield return System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        yield return System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
        yield return System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
        yield return System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
        yield return System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        yield return System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        yield return System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserProfileRoot(string path)
    {
        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains(
                   $"Google{Path.DirectorySeparatorChar}Chrome{Path.DirectorySeparatorChar}User Data",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(
                   $"Microsoft{Path.DirectorySeparatorChar}Edge{Path.DirectorySeparatorChar}User Data",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(
                   $"Mozilla{Path.DirectorySeparatorChar}Firefox{Path.DirectorySeparatorChar}Profiles",
                   StringComparison.OrdinalIgnoreCase);
    }
}
