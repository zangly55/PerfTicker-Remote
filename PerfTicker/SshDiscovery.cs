using System.Text.RegularExpressions;

namespace PerfTicker;

internal sealed record SshProfile(string Alias, string Host, int Port, string User, string KeyPath, bool NeedsProxy)
{
    public override string ToString() => $"{Alias} — {Host}:{Port}" + (NeedsProxy ? "（需要跳板）" : "");
}

// Read common OpenSSH options without executing ssh, Match exec or ProxyCommand.
internal static class SshDiscovery
{
    private sealed record Section(string[] Patterns, Dictionary<string, string> Options);
    private static string[] Words(string value) => Regex.Matches(value, "\"[^\"]*\"|'[^']*'|[^\\s#]+")
        .Select(m => m.Value.Trim('"', '\'')).ToArray();
    private static bool Match(string alias, string pattern) => Regex.IsMatch(alias,
        "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    public static List<SshProfile> Read(out string note)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(home, ".ssh", "config");
        note = "读取此电脑的 ~/.ssh/config；也可直接填写 IP。";
        if (!File.Exists(path)) { note = "未找到 SSH config，请手动输入主机/IP、端口和账号。"; return new(); }
        try { return Parse(File.ReadAllText(path), home, out note); }
        catch (Exception ex) { note = "无法读取 SSH config：" + ex.Message; return new(); }
    }

    internal static List<SshProfile> Parse(string text, string home, out string note)
    {
        var sections = new List<Section>();
        var section = new Section(new[] { "*" }, new(StringComparer.OrdinalIgnoreCase));
        sections.Add(section);
        var aliases = new List<string>();
        bool inMatch = false, unsupported = false;
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"^([^\s=#]+)[\s=]+(.*)$");
            if (!match.Success) continue;
            string key = match.Groups[1].Value, value = match.Groups[2].Value.TrimStart('=', ' ', '\t');
            var words = Words(value);
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                inMatch = false;
                section = new Section(words, new(StringComparer.OrdinalIgnoreCase));
                sections.Add(section);
                aliases.AddRange(words.Where(w => !w.Contains('*') && !w.Contains('?') && !w.StartsWith('!')));
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase)) { inMatch = true; unsupported = true; }
            else if (key.Equals("Include", StringComparison.OrdinalIgnoreCase)) unsupported = true;
            else if (!inMatch && words.Length > 0) section.Options.TryAdd(key, words[0]);
        }
        var result = new List<SshProfile>();
        foreach (string alias in aliases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in sections)
            {
                if (block.Patterns.Any(p => p.StartsWith('!') && Match(alias, p[1..]))) continue;
                if (!block.Patterns.Any(p => !p.StartsWith('!') && Match(alias, p))) continue;
                foreach (var pair in block.Options) options.TryAdd(pair.Key, pair.Value);
            }
            string Get(string key, string fallback = "") => options.GetValueOrDefault(key, fallback);
            string host = Get("HostName", alias).Replace("%h", alias).Replace("%n", alias);
            string user = Get("User", Environment.UserName);
            string identity = Get("IdentityFile").Replace("%d", home).Replace("%h", host).Replace("%r", user);
            if (identity.StartsWith("~/") || identity.StartsWith("~\\")) identity = Path.Combine(home, identity[2..]);
            if (identity.Length > 0 && !Path.IsPathRooted(identity)) identity = Path.Combine(home, identity);
            bool proxy = new[] { "ProxyJump", "ProxyCommand" }.Any(k => options.ContainsKey(k) && Get(k) != "none");
            int port = int.TryParse(Get("Port", "22"), out int parsed) && parsed is > 0 and <= 65535 ? parsed : 22;
            result.Add(new(alias, host, port, user, identity, proxy));
        }
        note = $"识别到 {result.Count} 个 SSH 主机；选择后可修改地址。" +
            (unsupported ? " Include / Match 未展开，请核对连接参数。" : "");
        return result;
    }
}
