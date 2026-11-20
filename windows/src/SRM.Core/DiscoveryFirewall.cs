using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using SRM.Core.Localization;

namespace SRM.Core;

/// <summary>
/// 「局域网里别人搜不到这台机器」的另一半原因：Windows 防火墙。
/// 防火墙按网卡所在网络的“位置”（专用 / 公用 / 域）分别套规则；LocalSend 这类工具装出来的
/// 「阻止」入站规则只针对公用网络，而「阻止」永远优先于「放行」——所以哪怕发现端口 53317
/// 早就放行了，只要这块网卡被系统当成公用网络，别人发过来的发现包还是会被悄悄丢掉：
/// 结果就是「我能看到别人，别人看不到我」。
/// 这里负责把这些规则读出来，并在需要时改成本机能被局域网发现的状态。
/// </summary>
public static class DiscoveryFirewall
{
    /// <summary>SRM 自己建的放行规则名（改完还能靠它认出来）。这个名字故意不跟语言变：换了名字就认不出旧规则，
    /// 会在防火墙里留下一堆同名不同语言的历史规则。</summary>
    public const string OwnRuleName = "SRM 局域网发现（LocalSend 53317）";

    private static string RuleGroupText => Loc.T("Core.Fw.RuleGroup");
    private const string FirewallRulesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    /// <summary>防火墙里一条入站规则的摘要。</summary>
    public sealed class RuleInfo
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public List<string> Profiles { get; init; } = new();
        public string Protocol { get; init; } = string.Empty;
        public string LocalPort { get; init; } = string.Empty;
        public string Program { get; init; } = string.Empty;

        public string ProtocolText => Protocol switch
        {
            "6" => "TCP",
            "17" => "UDP",
            _ => Protocol.Length > 0 ? Loc.F("Core.Fw.Protocol", Protocol) : Loc.T("Core.Fw.AllProtocols"),
        };

        public string ProfileText => Profiles.Count == 0
            ? Loc.T("Core.Fw.Profiles.Any")
            : string.Join(Loc.T("Core.List.Separator"), Profiles.Select(ProfileTextOf));

        public static string ProfileTextOf(string profile) => profile switch
        {
            "Domain" => Loc.T("Core.Fw.Profile.Domain"),
            "Private" => Loc.T("Core.Fw.Profile.Private"),
            "Public" => Loc.T("Core.Fw.Profile.Public"),
            "Any" => Loc.T("Core.Fw.Profiles.Any"),
            _ => profile,
        };
    }

    /// <summary>这台机器“能不能被别人发现”相关的状态。</summary>
    public sealed class Status
    {
        public bool Readable { get; init; }
        public List<RuleInfo> BlockingRules { get; init; } = new();
        public bool PortAllowed { get; init; }
        public bool OwnRuleReady { get; init; }
        public string Category { get; init; } = string.Empty;
        public string NetworkName { get; init; } = string.Empty;
        public List<int> Joined { get; init; } = new();

        public bool ProfileKnown => Category.Length > 0;
        public bool OnPublicProfile => string.Equals(Category, "Public", StringComparison.OrdinalIgnoreCase);
        public bool NeedsFix => Readable && (BlockingRules.Count > 0 || !PortAllowed);

        public string CategoryText => Category switch
        {
            "Private" => Loc.T("Core.Fw.Profile.Private"),
            "Public" => Loc.T("Core.Fw.Profile.Public"),
            "DomainAuthenticated" => Loc.T("Core.Fw.Profile.Domain"),
            _ => Loc.T("Core.Fw.Profile.Unknown"),
        };

        /// <summary>这些「阻止」规则会挡住哪些网络位置（默认公用）。</summary>
        public string BlockedProfilesText
        {
            get
            {
                var profiles = new List<string>();
                foreach (var rule in BlockingRules)
                {
                    var names = rule.Profiles.Count == 0 ? new List<string> { "Any" } : rule.Profiles;
                    foreach (var name in names)
                    {
                        var text = RuleInfo.ProfileTextOf(name);
                        if (!profiles.Contains(text)) profiles.Add(text);
                    }
                }
                return string.Join(Loc.T("Core.List.Separator"), profiles);
            }
        }

        /// <summary>界面提示区用的一句话。</summary>
        public string ShortText()
        {
            if (!Readable) return Loc.T("Core.Fw.Short.Unreadable");
            if (BlockingRules.Count == 0 && PortAllowed) return Loc.T("Core.Fw.Short.Allowed");
            if (BlockingRules.Count > 0)
            {
                var count = BlockingRules.Count;
                var profiles = BlockedProfilesText.Length > 0 ? BlockedProfilesText : Loc.T("Core.Fw.Profile.Public");
                return OnPublicProfile
                    ? Loc.F("Core.Fw.Short.BlockedOnProfile", CategoryText, count)
                    : Loc.F("Core.Fw.Short.BlockedOffProfile", profiles, count);
            }
            return Loc.T("Core.Fw.Short.NoAllowRule");
        }

        /// <summary>详细说明（弹窗、鼠标悬停用）。</summary>
        public string Describe()
        {
            if (!Readable) return Loc.T("Core.Fw.Describe.Unreadable");
            var lines = new List<string>();
            if (BlockingRules.Count > 0)
            {
                lines.Add(Loc.F("Core.Fw.Describe.BlocksHeader", BlockingRules.Count));
                foreach (var rule in BlockingRules)
                {
                    var who = rule.Program.Length > 0 ? rule.Program : rule.Name;
                    lines.Add(Loc.F("Core.Fw.Describe.RuleLine", rule.ProtocolText, rule.ProfileText, who));
                }
                lines.Add(Loc.T("Core.Fw.Describe.BlockPriority"));
                lines.Add(ProfileKnown
                    ? Loc.F(OnPublicProfile ? "Core.Fw.Describe.CurrentBlocked" : "Core.Fw.Describe.CurrentOk", CategoryText)
                    : Loc.T("Core.Fw.Describe.ProfileUnknown"));
            }
            else if (!PortAllowed)
            {
                lines.Add(Loc.F("Core.Fw.Describe.NoPortRule", DiscoveryRoute.Port));
            }
            else
            {
                lines.Add(Loc.F("Core.Fw.Describe.PortAllowed", DiscoveryRoute.Port));
            }
            if (OwnRuleReady) lines.Add(Loc.F("Core.Fw.Describe.OwnRule", OwnRuleName));
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>读所有入站防火墙规则（只读注册表，很快，不需要管理员权限）。</summary>
    public static List<RuleInfo> ReadRules()
    {
        var list = new List<RuleInfo>();
        using var key = Registry.LocalMachine.OpenSubKey(FirewallRulesKey);
        if (key is null) return list;
        foreach (var id in key.GetValueNames())
        {
            if (key.GetValue(id) is not string raw || raw.Length == 0) continue;
            var rule = ParseRule(id, raw);
            if (rule is not null) list.Add(rule);
        }
        return list;
    }

    /// <summary>读当前状态：会不会把别人的发现包丢掉、这块网卡在什么网络位置、哪些网卡在收喊话。</summary>
    public static Status Query(int interfaceIndex = 0, bool withJoins = false)
    {
        List<RuleInfo> rules;
        try
        {
            rules = ReadRules();
        }
        catch
        {
            rules = new List<RuleInfo>();
        }
        if (rules.Count == 0) return new Status { Readable = false };

        var blocking = rules.Where(rule => rule.Enabled && IsBlock(rule) && HitsDiscovery(rule)).ToList();
        var portAllowed = rules.Any(rule => rule.Enabled && !IsBlock(rule) && HitsDiscoveryPort(rule) && CoversAllProfiles(rule));
        var ownRule = rules.Any(rule => rule.Enabled && !IsBlock(rule) && string.Equals(rule.Name, OwnRuleName, StringComparison.OrdinalIgnoreCase));
        var (category, networkName) = ReadCategory(interfaceIndex);
        return new Status
        {
            Readable = true,
            BlockingRules = blocking,
            PortAllowed = portAllowed,
            OwnRuleReady = ownRule,
            Category = category,
            NetworkName = networkName,
            Joined = withJoins ? QueryGroupMembers() : new List<int>(),
        };
    }

    /// <summary>哪些网卡已经加入了局域网发现用的组播组（说明发现软件在这些网卡上收得到喊话）。</summary>
    public static List<int> QueryGroupMembers()
    {
        var list = new List<int>();
        var (code, output) = RunCaptured("netsh.exe", new[] { "interface", "ipv4", "show", "joins" });
        if (code != 0 || output.Length == 0) return list;
        var current = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3 && Ipv4.TryParse(tokens[^1], out var address))
            {
                if (address == DiscoveryRoute.Group && current > 0 && !list.Contains(current)) list.Add(current);
                continue;
            }
            var header = ParseInterfaceHeader(line);
            if (header > 0) current = header;
        }
        list.Sort();
        return list;
    }

    /// <summary>
    /// 一键修复（需要管理员权限）：停掉会丢包的「阻止」规则、把发现端口放行到所有网络位置、
    /// 必要时把这块网卡所在的网络位置改成「专用」。返回每一步做了什么，供界面显示。
    /// </summary>
    public static List<string> Repair(int interfaceIndex)
    {
        if (!NetworkApi.IsElevated) throw new InvalidOperationException(Loc.T("Core.Fw.NeedsAdmin"));
        var (code, output) = RunCaptured("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", RepairScript(interfaceIndex),
        });
        OperationLog.Write(Loc.F("Core.Fw.RepairLog", interfaceIndex, code, output.Length > 0 ? output : Loc.T("Core.Empty")));
        var steps = DescribeReport(output);
        if (code != 0 && steps.Count == 0)
        {
            var text = output.Trim();
            throw new InvalidOperationException(text.Length > 0 ? text : Loc.T("Core.Fw.RepairFailed"));
        }
        return steps;
    }

    // ================= 内部实现 =================

    private static bool IsBlock(RuleInfo rule) => string.Equals(rule.Action, "Block", StringComparison.OrdinalIgnoreCase);

    /// <summary>这条规则跟「局域网发现」有没有关系：认程序的路径、规则名和用的端口。</summary>
    private static bool HitsDiscovery(RuleInfo rule) => HitsDiscoveryPort(rule)
        || rule.Program.Contains("localsend", StringComparison.OrdinalIgnoreCase)
        || rule.Name.Contains("localsend", StringComparison.OrdinalIgnoreCase);

    /// <summary>这条规则管不管发现端口（53317）。</summary>
    private static bool HitsDiscoveryPort(RuleInfo rule)
    {
        var ports = rule.LocalPort.Length > 0 ? rule.LocalPort : rule.Name.Contains(DiscoveryRoute.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ? DiscoveryRoute.Port.ToString(CultureInfo.InvariantCulture) : string.Empty;
        if (ports.Length == 0) return false;
        foreach (var part in ports.Split(','))
        {
            var text = part.Trim();
            var dash = text.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(text[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var low)
                    && int.TryParse(text[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var high)
                    && DiscoveryRoute.Port >= low && DiscoveryRoute.Port <= high) return true;
                continue;
            }
            if (text == DiscoveryRoute.Port.ToString(CultureInfo.InvariantCulture)) return true;
        }
        return false;
    }

    /// <summary>这条规则是不是在所有网络位置（专用 / 公用 / 域）都生效。</summary>
    private static bool CoversAllProfiles(RuleInfo rule)
    {
        if (rule.Profiles.Count == 0) return true;
        if (rule.Profiles.Any(profile => string.Equals(profile, "Any", StringComparison.OrdinalIgnoreCase))) return true;
        return rule.Profiles.Any(profile => string.Equals(profile, "Private", StringComparison.OrdinalIgnoreCase))
            && rule.Profiles.Any(profile => string.Equals(profile, "Public", StringComparison.OrdinalIgnoreCase));
    }

    private static RuleInfo? ParseRule(string id, string raw)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<string>();
        foreach (var part in raw.Split('|'))
        {
            var split = part.IndexOf('=');
            if (split <= 0) continue;
            var name = part[..split];
            var value = part[(split + 1)..];
            if (string.Equals(name, "Profile", StringComparison.OrdinalIgnoreCase)) profiles.Add(value);
            else if (!fields.ContainsKey(name)) fields[name] = value;
        }
        if (!fields.TryGetValue("Dir", out var dir) || !string.Equals(dir, "In", StringComparison.OrdinalIgnoreCase)) return null;
        var active = fields.TryGetValue("Active", out var flag) ? flag : "TRUE";
        return new RuleInfo
        {
            Id = id,
            Name = fields.TryGetValue("Name", out var name2) ? name2 : string.Empty,
            Action = fields.TryGetValue("Action", out var action) ? action : string.Empty,
            Enabled = !string.Equals(active, "FALSE", StringComparison.OrdinalIgnoreCase),
            Profiles = profiles,
            Protocol = fields.TryGetValue("Protocol", out var protocol) ? protocol : string.Empty,
            LocalPort = fields.TryGetValue("LPort", out var port) ? port : string.Empty,
            Program = fields.TryGetValue("App", out var program) ? program : string.Empty,
        };
    }

    /// <summary>读这块网卡所在网络的“位置”（专用 / 公用 / 域）；需要管理员权限，读不到就返回空。</summary>
    private static (string Category, string Name) ReadCategory(int interfaceIndex)
    {
        if (interfaceIndex <= 0 || !NetworkApi.IsElevated) return (string.Empty, string.Empty);
        var index = interfaceIndex.ToString(CultureInfo.InvariantCulture);
        var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; try { "
            + $"$c = Get-NetConnectionProfile -InterfaceIndex {index} -ErrorAction Stop | Select-Object -First 1; "
            + "'category=' + [string]$c.NetworkCategory; 'name=' + [string]$c.Name } catch { 'category='; 'name=' }";
        var (code, output) = RunCaptured("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script });
        if (code != 0 && output.Length == 0) return (string.Empty, string.Empty);
        var category = string.Empty;
        var name = string.Empty;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith("category=", StringComparison.OrdinalIgnoreCase)) category = line[9..].Trim();
            else if (line.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) name = line[5..].Trim();
        }
        return (category, name);
    }

    /// <summary>从 netsh 的一句“Interface 7: WLAN”里取出网卡序号。</summary>
    private static int ParseInterfaceHeader(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return 0;
        var end = colon - 1;
        while (end >= 0 && line[end] == ' ') end--;
        var start = end;
        while (start >= 0 && char.IsDigit(line[start])) start--;
        if (start == end) return 0;
        return int.TryParse(line[(start + 1)..(end + 1)], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// 拼出修复用的 PowerShell 脚本（需要管理员权限运行）。
    /// 公开出来是为了能先看它到底要动什么，或者在改成别的用法时贴出来核对。
    /// </summary>
    public static string RepairScript(int interfaceIndex)
    {
        var index = interfaceIndex > 0 ? interfaceIndex.ToString(CultureInfo.InvariantCulture) : "0";
        var port = DiscoveryRoute.Port.ToString(CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'Continue'");
        builder.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
        builder.AppendLine($"$idx = {index}");
        builder.AppendLine($"$own = '{OwnRuleName}'");
        builder.AppendLine("if ($idx -gt 0) {");
        builder.AppendLine("  try {");
        builder.AppendLine("    $c = Get-NetConnectionProfile -InterfaceIndex $idx -ErrorAction Stop | Select-Object -First 1");
        builder.AppendLine("    if ($c.NetworkCategory -eq 'Private') { 'profile=keep|' + $c.NetworkCategory }");
        builder.AppendLine("    else { $old = [string]$c.NetworkCategory; Set-NetConnectionProfile -InterfaceIndex $idx -NetworkCategory Private -ErrorAction Stop; 'profile=set|' + $old }");
        builder.AppendLine("  } catch { 'profile=skip|' + $_.Exception.Message }");
        builder.AppendLine("}");
        builder.AppendLine("try {");
        builder.AppendLine("  $blocks = @(Get-NetFirewallRule -Direction Inbound -Action Block -Enabled True -ErrorAction Stop | Where-Object { $_.DisplayName -like '*localsend*' -or $_.DisplayName -like '*" + port + "*' })");
        builder.AppendLine("  foreach ($r in $blocks) { Disable-NetFirewallRule -Name $r.Name -ErrorAction Stop | Out-Null }");
        builder.AppendLine("  'blocks=disabled|' + $blocks.Count + '|' + (($blocks | ForEach-Object { $_.DisplayName }) -join ',')");
        builder.AppendLine("} catch { 'blocks=skip|' + $_.Exception.Message }");
        builder.AppendLine("try {");
        builder.AppendLine("  Remove-NetFirewallRule -DisplayName $own -ErrorAction SilentlyContinue");
        builder.AppendLine($"  New-NetFirewallRule -DisplayName $own -Group '{RuleGroupText}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} -Profile Any -ErrorAction Stop | Out-Null");
        builder.AppendLine($"  New-NetFirewallRule -DisplayName $own -Group '{RuleGroupText}' -Direction Inbound -Action Allow -Protocol UDP -LocalPort {port} -Profile Any -ErrorAction Stop | Out-Null");
        builder.AppendLine("  'allow=ok'");
        builder.AppendLine("} catch { 'allow=skip|' + $_.Exception.Message }");
        return builder.ToString();
    }

    private static List<string> DescribeReport(string output)
    {
        var steps = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            var split = line.IndexOf('=');
            if (split <= 0)
            {
                steps.Add(line);
                continue;
            }
            var head = line[..split];
            var parts = line[(split + 1)..].Split('|');
            var kind = parts[0];
            var value = parts.Length > 1 ? parts[1] : string.Empty;
            var extra = parts.Length > 2 ? parts[2] : string.Empty;
            switch (head)
            {
                case "profile" when kind == "set":
                    steps.Add(Loc.F("Core.Fw.Step.ProfileSet", RuleInfo.ProfileTextOf(value)));
                    break;
                case "profile" when kind == "keep":
                    steps.Add(Loc.T("Core.Fw.Step.ProfileKept"));
                    break;
                case "profile":
                    steps.Add(Loc.F("Core.Fw.Step.ProfileFailed", value));
                    break;
                case "blocks" when kind == "disabled":
                    var count = value.Length > 0 ? value : "0";
                    var names = extra.Length > 0 ? Loc.F("Core.Fw.Step.BlocksNames", extra) : string.Empty;
                    steps.Add(Loc.F("Core.Fw.Step.BlocksDisabled", count, names));
                    break;
                case "blocks":
                    steps.Add(Loc.F("Core.Fw.Step.BlocksFailed", value));
                    break;
                case "allow" when kind == "ok":
                    steps.Add(Loc.F("Core.Fw.Step.AllowAdded", DiscoveryRoute.Port));
                    break;
                case "allow":
                    steps.Add(Loc.F("Core.Fw.Step.AllowFailed", value));
                    break;
                default:
                    steps.Add(line);
                    break;
            }
        }
        return steps;
    }

    private static (int Code, string Output) RunCaptured(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) return (-1, string.Empty);
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return (process.ExitCode, output.Trim());
    }
}
