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

        Assert.True(releaseJob > 0);
        Assert.Contains("- 'v*'", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("validate-release-version.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-RequireGitTag", workflow, StringComparison.Ordinal);
        Assert.Contains("build-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("ReplicaSetup-${{ steps.version.outputs.version }}.exe", workflow, StringComparison.Ordinal);
        Assert.Contains("ReplicaSetup.exe.sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("--generate-notes", workflow, StringComparison.Ordinal);
        Assert.Contains("--prerelease", workflow, StringComparison.Ordinal);
        Assert.Contains("attest-build-provenance@", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", workflow[..releaseJob], StringComparison.Ordinal);
        Assert.Contains("contents: write", workflow[releaseJob..], StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", workflow, StringComparison.Ordinal);
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
    public void ReleaseScripts_VerifyTagVersionInstallerNamesAndChecksumsWithoutDynamicEvaluation()
    {
        string validator = File.ReadAllText(RepositoryFile("scripts", "validate-release-version.ps1"));
        string assets = File.ReadAllText(RepositoryFile("scripts", "prepare-release-assets.ps1"));

        Assert.Contains("Directory.Build.props", validator, StringComparison.Ordinal);
        Assert.Contains("alpha|beta|rc", validator, StringComparison.Ordinal);
        Assert.Contains("must be an annotated tag", validator, StringComparison.Ordinal);
        Assert.Contains("ReplicaSetup-$Version.exe", assets, StringComparison.Ordinal);
        Assert.Contains("ReplicaSetup.exe", assets, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", assets, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", validator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", assets, StringComparison.OrdinalIgnoreCase);
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

    [GeneratedRegex("^(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+@[0-9a-f]{40}(?:\\s+#\\s+v[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ImmutableActionReference();
}
