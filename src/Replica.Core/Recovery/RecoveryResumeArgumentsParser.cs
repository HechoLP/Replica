namespace Replica.Core.Recovery;

public static class RecoveryResumeArgumentsParser
{
    public const string ResumeSwitch = "--resume-recovery";

    public static bool TryParse(IReadOnlyList<string> arguments, out string? sessionId)
    {
        sessionId = null;
        if (arguments.Count != 2 ||
            !string.Equals(arguments[0], ResumeSwitch, StringComparison.Ordinal) ||
            !Guid.TryParseExact(arguments[1], "N", out Guid parsed))
        {
            return false;
        }

        sessionId = parsed.ToString("N");
        return true;
    }
}
