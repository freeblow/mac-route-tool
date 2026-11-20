using System.Diagnostics;
using SRM.Core.Localization;

namespace SRM.Core;

public sealed class RouteService
{
    public RouteConfig Config { get; private set; } = new();
    public Dictionary<string, string> Errors { get; } = new();
    public DateTime LastSync { get; private set; } = DateTime.MinValue;

    public void Reload()
    {
        Config = RouteConfigStore.Load();
        Errors.Clear();
    }

    public void Save() => RouteConfigStore.Save(Config);

    public RouteStatus GetStatus(RouteEntry route)
    {
        if (!route.Enabled) return new RouteStatus { State = RouteState.Disabled };
        if (Errors.TryGetValue(route.Id, out var error)) return new RouteStatus { State = RouteState.Failed, Message = error };
        if (!Ipv4.TryParseSubnet(route.Destination, out var network, out var mask) || !Ipv4.TryParse(route.Gateway, out var gateway))
        {
            return new RouteStatus { State = RouteState.Failed, Message = Loc.T("Core.Route.BadTargetOrGateway") };
        }
        var prefix = Ipv4.PrefixFromMask(mask);
        var active = FindActiveRoute(network, prefix, gateway, route.InterfaceIndex);
        if (active is null) return new RouteStatus { State = RouteState.Pending };
        var persistent = PersistentRoutes.Contains(network, mask, gateway);
        return persistent
            ? new RouteStatus { State = RouteState.Active }
            : new RouteStatus { State = RouteState.Active, Message = Loc.T("Core.Route.ActiveNotPersistent") };
    }

    public void Sync()
    {
        foreach (var route in Config.Routes)
        {
            try
            {
                EnsureRoute(route);
                Errors.Remove(route.Id);
            }
            catch (Exception ex)
            {
                Errors[route.Id] = ex.Message;
            }
        }
        LastSync = DateTime.Now;
    }

    public void EnsureRoute(RouteEntry route)
    {
        if (!route.Enabled) return;
        if (!Ipv4.TryParseSubnet(route.Destination, out var network, out var mask))
        {
            throw new InvalidOperationException(Loc.T("Core.Route.BadDestination"));
        }
        if (!Ipv4.TryParse(route.Gateway, out var gateway))
        {
            throw new InvalidOperationException(Loc.T("Core.Route.BadGateway"));
        }
        var prefix = Ipv4.PrefixFromMask(mask);
        var active = FindActiveRoute(network, prefix, gateway, route.InterfaceIndex);
        var persistent = PersistentRoutes.Contains(network, mask, gateway);
        if (active is not null && persistent) return;
        if (active is not null && !persistent) DeleteRoute(route);
        AddPersistentRoute(route);
    }

    public void RemoveRoute(RouteEntry route)
    {
        if (!Ipv4.TryParseSubnet(route.Destination, out var network, out var mask)) return;
        if (!Ipv4.TryParse(route.Gateway, out var gateway)) return;
        DeleteRoute(route);
    }

    public void AddPersistentRoute(RouteEntry route)
    {
        if (!Ipv4.TryParseSubnet(route.Destination, out var network, out var mask))
        {
            throw new InvalidOperationException(Loc.T("Core.Route.BadDestination"));
        }
        if (!Ipv4.TryParse(route.Gateway, out var gateway))
        {
            throw new InvalidOperationException(Loc.T("Core.Route.BadGateway"));
        }
        var arguments = new List<string>
        {
            "-p", "add", Ipv4.ToText(network), "mask", Ipv4.ToText(mask), Ipv4.ToText(gateway),
        };
        if (route.InterfaceIndex is > 0) arguments.AddRange(new[] { "IF", route.InterfaceIndex.Value.ToString() });
        var (code, output) = RunProcess("route.exe", arguments);
        if (code != 0) throw new InvalidOperationException(DescribeRouteError(output, route));
    }

    public void DeleteRoute(RouteEntry route)
    {
        if (!Ipv4.TryParseSubnet(route.Destination, out var network, out var mask)) return;
        if (!Ipv4.TryParse(route.Gateway, out var gateway)) return;
        var arguments = new List<string>
        {
            "delete", Ipv4.ToText(network), "mask", Ipv4.ToText(mask), Ipv4.ToText(gateway),
        };
        if (route.InterfaceIndex is > 0) arguments.AddRange(new[] { "IF", route.InterfaceIndex.Value.ToString() });
        RunProcess("route.exe", arguments);
        PersistentRoutes.Remove(network, mask, gateway);
    }

    private static RouteRow? FindActiveRoute(uint network, int prefixLength, uint gateway, int? interfaceIndex)
    {
        List<RouteRow> rows;
        try
        {
            rows = RouteTable.Query();
        }
        catch
        {
            return null;
        }
        foreach (var row in rows)
        {
            if (row.Destination != network || row.PrefixLength != prefixLength) continue;
            if (row.NextHop != gateway) continue;
            if (interfaceIndex is > 0 && row.InterfaceIndex != interfaceIndex) continue;
            return row;
        }
        return null;
    }

    private static string DescribeRouteError(string output, RouteEntry route)
    {
        var text = (output ?? string.Empty).Trim();
        if (text.Length == 0) return Loc.F("Core.Route.WriteFailed", route.Destination);
        return text.Length > 300 ? text[..300] : text;
    }

    public static (int Code, string Output) RunProcess(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) return (-1, Loc.F("Core.Route.RunFailed", fileName));
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(20000);
        return (process.ExitCode, output);
    }
}
