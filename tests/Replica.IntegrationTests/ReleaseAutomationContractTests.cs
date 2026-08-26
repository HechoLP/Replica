using System.Text.RegularExpressions;

namespace Replica.IntegrationTests;

public sealed partial class ReleaseAutomationContractTests
{
    [Fact]
    public void ContinuousIntegration_HasRequestedTriggersCacheAndOrderedChecks()
    {
        string workflow = File.ReadAllText(RepositoryFile(".github", "workflows", "ci.yml"));

        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("cache: true", workflow, StringComparison.Ordinal);
        AssertOrdered(
            workflow,
            "- name: Restore",
            "- name: Verify formatting",
            "- name: Build",
            "- name: Test",
            "- name: Audit vulnerable packages",
            "- name: Upload test results");
        AssertPinnedOfficialActions(workflow);
    }

    [Fact]
    public void CodeQl_AnalyzesCSharpOnPullRequestsDefaultBranchAndSchedule()
    {
        string workflow = File.ReadAllText(RepositoryFile(".github", "workflows", "codeql.yml"));

        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("push:", workflow, StringComparison.Ordinal);
        Assert.Contains("schedule:", workflow, StringComparison.Ordinal);
        Assert.Contains("languages: csharp", workflow, StringComparison.Ordinal);
        Assert.Contains("security-events: write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("language: actions", workflow, StringComparison.Ordinal);
        AssertPinnedOfficialActions(workflow);
    }

    [Fact]
    public void Release_ValidatesVersionPackagesCanonicalInstallerAndLimitsWritePermission()
    {
        string workflow = File.ReadAllText(RepositoryFile(".github", "workflows", "release.yml"));
        int releaseJob = workflow.IndexOf("\n  release:\n", StringComparison.Ordinal);
        int buildWindowsJob = workflow.IndexOf("\n  build-windows:\n", StringComparison.Ordinal);
        int packageWindowsJob = workflow.IndexOf("\n  package:\n", StringComparison.Ordinal);
        int buildMacJob = workflow.IndexOf("\n  build-macos:\n", StringComparison.Ordinal);
        int packageMacJob = workflow.IndexOf("\n  package-macos:\n", StringComparison.Ordinal);

        Assert.True(releaseJob > 0);
        Assert.DoesNotContain("push:\n    tags:", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("validate-release-version.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-RequireGitTag", workflow, StringComparison.Ordinal);
        Assert.Contains("github.event.repository.default_branch", workflow, StringComparison.Ordinal);
        Assert.Contains("-RequiredAncestorRef", workflow, StringComparison.Ordinal);
        Assert.Contains("git check-ref-format --branch", workflow, StringComparison.Ordinal);
        Assert.Contains("merge-base --is-ancestor", workflow, StringComparison.Ordinal);
        Assert.Contains("commit_sha: ${{ steps.version.outputs.commit_sha }}", workflow, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(workflow, "ref: \\$\\{\\{ needs\\.build-windows\\.outputs\\.commit_sha \\}\\}", RegexOptions.CultureInvariant).Count);
        Assert.Contains("environment: release-signing", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: release-publishing", workflow, StringComparison.Ordinal);
        Assert.Contains("UsePreparedPublish = $true", workflow, StringComparison.Ordinal);
        Assert.Contains("prepared-release.json", workflow, StringComparison.Ordinal);
        Assert.Contains("replicaExeSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("replicaExecutableSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("REPLICA_WINDOWS_PUBLISHER_CERTIFICATE_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("build-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("REPLICA_WINDOWS_SIGNING_PFX_BASE64", workflow, StringComparison.Ordinal);
        Assert.Contains("A Stable release requires the protected Windows Authenticode signing identity.", workflow, StringComparison.Ordinal);
        Assert.Contains("ReplicaPublisherCertificateSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("ReplicaSetup-${{ needs.build-windows.outputs.version }}.exe", workflow, StringComparison.Ordinal);
        Assert.Contains("ReplicaSetup.exe.sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("package-macos:", workflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: macos-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("build-macos-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("REPLICA_APPLE_DEVELOPER_ID_P12_BASE64", workflow, StringComparison.Ordinal);
        Assert.Contains("REPLICA_APPLE_DEVELOPER_ID_CERTIFICATE_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("A Stable release requires Apple Developer ID signing and notarization credentials.", workflow, StringComparison.Ordinal);
        Assert.Contains("notarytool store-credentials", workflow, StringComparison.Ordinal);
        Assert.Contains("NotaryKeychainPath", workflow, StringComparison.Ordinal);
        Assert.Contains("Replica-macOS-arm64.dmg", workflow, StringComparison.Ordinal);
        Assert.Contains("Replica-macOS-x64.dmg", workflow, StringComparison.Ordinal);
        Assert.Contains("--generate-notes", workflow, StringComparison.Ordinal);
        Assert.Contains("--prerelease", workflow, StringComparison.Ordinal);
        Assert.Contains("attest-build-provenance@", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", workflow[..releaseJob], StringComparison.Ordinal);
        Assert.Contains("contents: write", workflow[releaseJob..], StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", workflow, StringComparison.Ordinal);
        Assert.True(buildWindowsJob > 0 && packageWindowsJob > buildWindowsJob);
        Assert.True(buildMacJob > packageWindowsJob && packageMacJob > buildMacJob);
        Assert.DoesNotContain("secrets.", workflow[buildWindowsJob..packageWindowsJob], StringComparison.Ordinal);
        Assert.DoesNotContain("environment: release-signing", workflow[buildWindowsJob..packageWindowsJob], StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", workflow[buildMacJob..packageMacJob], StringComparison.Ordinal);
        Assert.DoesNotContain("environment: release-signing", workflow[buildMacJob..packageMacJob], StringComparison.Ordinal);
        Assert.Contains("REPLICA_INNO_SETUP_TOOL_TREE_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("REPLICA_INNO_SETUP_PUBLISHER_CERTIFICATE_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("get-directory-tree-sha256.ps1", workflow, StringComparison.Ordinal);
        AssertOrdered(
            workflow,
            "- name: Test",
            "- name: Configure protected Windows signing identity",
            "- name: Sign prepared application and build signed installer",
            "- name: Remove protected Windows signing identity and prove absence",
            "- name: Upload release package");
        AssertOrdered(
            workflow,
            "- name: Build and test macOS projects",
            "- name: Configure protected Apple signing and notarization identities",
            "- name: Create signed app bundle and notarized DMG",
            "- name: Remove protected Apple signing material and prove absence",
            "- name: Upload macOS release package");
        AssertPinnedOfficialActions(workflow);
    }

    [Fact]
    public void Dependabot_UpdatesNuGetAndActionsWeeklyInGroups()
    {
        string configuration = File.ReadAllText(RepositoryFile(".github", "dependabot.yml"));

        Assert.Contains("package-ecosystem: nuget", configuration, StringComparison.Ordinal);
        Assert.Contains("package-ecosystem: github-actions", configuration, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(configuration, "interval: weekly", RegexOptions.CultureInvariant).Count);
        Assert.Equal(2, Regex.Matches(configuration, "open-pull-requests-limit: 5", RegexOptions.CultureInvariant).Count);
        Assert.Contains("groups:", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowActionPins_AcceptVersionCommentsWithoutRelaxingCommitPins()
    {
        const string pinnedReference =
            "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1";

        Assert.Matches(ImmutableActionReference(), pinnedReference);
        Assert.DoesNotMatch(ImmutableActionReference(), "actions/checkout@v7.0.1");
    }

    [Fact]
    public void ReleaseScripts_VerifyTagVersionInstallerNamesAndChecksumsWithoutDynamicEvaluation()
    {
        string validator = File.ReadAllText(RepositoryFile("scripts", "validate-release-version.ps1"));
        string assets = File.ReadAllText(RepositoryFile("scripts", "prepare-release-assets.ps1"));
        string windowsAssets = File.ReadAllText(RepositoryFile("scripts", "build-release.ps1"));
        string installerTest = File.ReadAllText(RepositoryFile("scripts", "test-installer.ps1"));
        string macAssets = File.ReadAllText(RepositoryFile("scripts", "build-macos-release.ps1"));
        string installer = File.ReadAllText(RepositoryFile("installer", "Replica.iss"));

        Assert.Contains("Directory.Build.props", validator, StringComparison.Ordinal);
        Assert.Contains("alpha|beta|rc", validator, StringComparison.Ordinal);
        Assert.Contains("must be an annotated tag", validator, StringComparison.Ordinal);
        Assert.Contains("required protected-branch reference is invalid", validator, StringComparison.Ordinal);
        Assert.Contains("merge-base --is-ancestor", validator, StringComparison.Ordinal);
        Assert.Contains("ReplicaSetup-$Version.exe", assets, StringComparison.Ordinal);
        Assert.Contains("ReplicaSetup.exe", assets, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", assets, StringComparison.Ordinal);
        Assert.Contains("ReplicaPublisherCertificateSha256", windowsAssets, StringComparison.Ordinal);
        Assert.Contains("Get-CertificateSha256", windowsAssets, StringComparison.Ordinal);
        Assert.Contains("UsePreparedPublish", windowsAssets, StringComparison.Ordinal);
        Assert.Contains("prepared-release.json", windowsAssets, StringComparison.Ordinal);
        Assert.Contains("reviewed digest manifest", windowsAssets, StringComparison.Ordinal);
        Assert.Contains("ExpectedSignerCertificateSha256", installerTest, StringComparison.Ordinal);
        Assert.Contains("SignTool=replica", installer, StringComparison.Ordinal);
        Assert.Contains("SignedUninstaller=yes", installer, StringComparison.Ordinal);
        Assert.Contains("Replica-macOS-$Architecture.dmg", macAssets, StringComparison.Ordinal);
        Assert.Contains("codesign --force --deep --sign '-'", macAssets, StringComparison.Ordinal);
        Assert.Contains("codesign --force --deep --options runtime --timestamp", macAssets, StringComparison.Ordinal);
        Assert.Contains("notarytool", macAssets, StringComparison.Ordinal);
        Assert.Contains("stapler validate", macAssets, StringComparison.Ordinal);
        Assert.Contains("UsePreparedPublish", macAssets, StringComparison.Ordinal);
        Assert.Contains("prepared-release.json", macAssets, StringComparison.Ordinal);
        Assert.Contains("reviewed digest manifest", macAssets, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", macAssets, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", validator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", assets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", windowsAssets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", installerTest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", macAssets, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContinuousIntegration_IsolatesMacOsArchitecturesOnSeparateRunners()
    {
        string workflow = File.ReadAllText(RepositoryFile(".github", "workflows", "ci.yml"));

        Assert.Contains("architecture: [arm64, x64]", workflow, StringComparison.Ordinal);
        Assert.Contains("macOS ${{ matrix.architecture }} build and test", workflow, StringComparison.Ordinal);
        Assert.Contains("-Architecture ${{ matrix.architecture }}", workflow, StringComparison.Ordinal);
        Assert.Contains("macos-ci-package-${{ matrix.architecture }}", workflow, StringComparison.Ordinal);
        Assert.Contains("macos-test-results-${{ matrix.architecture }}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach ($architecture in @('arm64', 'x64'))", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerSandbox_IsOfflineReadOnlyAndRunsTheFixedInstallerSmokeTest()
    {
        string sandbox = File.ReadAllText(RepositoryFile("scripts", "test-installer-sandbox.ps1"));

        Assert.Contains("WindowsSandbox.exe", sandbox, StringComparison.Ordinal);
        Assert.Contains("<ReadOnly>true</ReadOnly>", sandbox, StringComparison.Ordinal);
        Assert.Contains("<Networking>Disable</Networking>", sandbox, StringComparison.Ordinal);
        Assert.Contains("<ClipboardRedirection>Disable</ClipboardRedirection>", sandbox, StringComparison.Ordinal);
        Assert.Contains("test-installer.ps1", sandbox, StringComparison.Ordinal);
        Assert.Contains("'-Install'", sandbox, StringComparison.Ordinal);
        Assert.Contains("sandbox-installer-test", sandbox, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", sandbox, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertOrdered(string value, params string[] markers)
    {
        int previous = -1;
        foreach (string marker in markers)
        {
            int current = value.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{marker}' after the preceding workflow step.");
            previous = current;
        }
    }

    private static void AssertPinnedOfficialActions(string workflow)
    {
        string[] actionReferences = workflow.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("uses:", StringComparison.Ordinal))
            .Select(line => line["uses:".Length..].Trim())
            .ToArray();

        Assert.NotEmpty(actionReferences);
        Assert.All(actionReferences, reference => Assert.Matches(ImmutableActionReference(), reference));
        Assert.All(actionReferences, reference => Assert.True(
            reference.StartsWith("actions/", StringComparison.Ordinal) ||
            reference.StartsWith("github/", StringComparison.Ordinal),
            $"Expected an official GitHub Action, but found '{reference}'."));
    }

    private static string RepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Replica.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }

    [GeneratedRegex("^(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+@[0-9a-f]{40}(?:\\s+#\\s+v[0-9]+(?:\\.[0-9]+){0,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ImmutableActionReference();
}
