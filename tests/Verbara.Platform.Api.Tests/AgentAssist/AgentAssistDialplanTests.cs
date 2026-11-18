namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end (design D3, D9): the shipped <c>docker/asterisk-config/extensions.conf</c> and the
/// demo override <c>docker/demo/demo-overrides/extensions.conf</c> send a snoop's audio to the address the acting
/// replica set on that snoop, <c>AUDIOSOCKET_HOST</c> (already <c>host:port</c>), with the snoop's own id. A port in
/// the dialplan would turn that address into <c>host:port:port</c>, which AudioSocket cannot dial, so no line may
/// carry one.
/// </summary>
public sealed class AgentAssistDialplanTests
{
    private const string ShippedPath = "docker/asterisk-config/extensions.conf";
    private const string DemoPath = "docker/demo/demo-overrides/extensions.conf";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Platform.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>The non-comment, non-blank lines of one dialplan context of the file at <paramref name="relativePath"/>.</summary>
    private static List<string> ContextLines(string relativePath, string name)
    {
        var lines = File.ReadAllLines(Path.Join([RepoRoot(), .. relativePath.Split('/')]));
        var result = new List<string>();
        var inside = false;
        foreach (var line in lines.Select(raw => raw.Split(';', 2)[0].Trim()).Where(line => line.Length > 0))
        {
            if (line.StartsWith('['))
            {
                inside = string.Equals(line, $"[{name}]", StringComparison.Ordinal);
                continue;
            }

            if (inside)
                result.Add(line);
        }

        return result;
    }

    [Theory]
    [InlineData(ShippedPath)]
    [InlineData(DemoPath)]
    public void AgentAssistContext_ShouldConnectToTheSnoopsAdvertisedAddress_WhenShipped(string path)
    {
        var lines = ContextLines(path, "agentassist");

        lines.Should().Equal(
            [
                "exten => audiosocket,1,AudioSocket(${AUDIOSOCKET_UUID},${AUDIOSOCKET_HOST})",
                "same => n,Hangup()",
            ],
            "{0} must dial the host:port the acting replica set on the snoop, with no port of its own", path);
    }
}
