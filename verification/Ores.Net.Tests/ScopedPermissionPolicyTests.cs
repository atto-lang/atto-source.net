using Ores.Net.Runtime;

internal static class ScopedPermissionPolicyTests
{
    private static void Assert(bool got, bool expected, string name)
    {
        if (got != expected) throw new Exception("Scoped permission policy: " + name);
    }

    public static void Run()
    {
        var empty = ScopedPermissionPolicy.DefaultDeny();
        foreach (var p in Enum.GetValues<ScopedPermissionPolicy.Kind>())
            Assert(empty.Allows(p, "/workspace/input.txt"), false, "default deny " + p);
        var file = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Read },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Read, "/workspace/data") });
        Assert(file.Allows(ScopedPermissionPolicy.Kind.Read, "/workspace/data/file.txt"), true, "path child");
        Assert(file.Allows(ScopedPermissionPolicy.Kind.Read, "/workspace/database/file"), false, "path segment");
        Assert(file.Allows(ScopedPermissionPolicy.Kind.Read, "/workspace/data "), false, "path whitespace");
        Assert(file.Allows(ScopedPermissionPolicy.Kind.Read, "/workspace/data/../secret"), false, "traversal");
        Assert(file.Allows(ScopedPermissionPolicy.Kind.Write, "/workspace/data/file"), false, "no write ceiling");
        var rootScope = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Read },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Read, "/") });
        Assert(rootScope.Allows(ScopedPermissionPolicy.Kind.Read, "/workspace/data"),
            true, "explicit root path scope");
        Assert(rootScope.Allows(ScopedPermissionPolicy.Kind.Read, "relative/path"),
            false, "root scope cannot admit relative paths");

        var env = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Env },
            new[] {
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Env, "*"),
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Env, "API_KEY", true)
            });
        Assert(env.Allows(ScopedPermissionPolicy.Kind.Env, "API_KEY"), false, "deny wins");
        Assert(env.Allows(ScopedPermissionPolicy.Kind.Env, "API_KEY "), true, "literal env space");
        Assert(env.Allows(ScopedPermissionPolicy.Kind.Env, "OTHER"), true, "wildcard env");

        var run = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Run },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Run, "git*") });
        Assert(run.Allows(ScopedPermissionPolicy.Kind.Run, "git-status"), true, "token prefix");
        Assert(run.Allows(ScopedPermissionPolicy.Kind.Run, "curl"), false, "token mismatch");

        var net = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "api.example.com:443") });
        Assert(net.Allows(ScopedPermissionPolicy.Kind.Net, "API.EXAMPLE.COM.:0443"), true, "canonical hostname/port");
        Assert(net.Allows(ScopedPermissionPolicy.Kind.Net, "api.example.com:444"), false, "port mismatch");
        Assert(net.Allows(ScopedPermissionPolicy.Kind.Net, "api.example.com:0"), false, "zero port");
        Assert(net.Allows(ScopedPermissionPolicy.Kind.Net, "127.1:443"), false, "IPv4 alias");
        Assert(net.Allows(ScopedPermissionPolicy.Kind.Net, "alice@api.example.com:443"), false, "credentials");
        var numericAlias = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] {
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "*"),
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net,
                    "127.0.0.1", true)
            });
        foreach (var alias in new[] {
            "127.0x0.0.1:443", "1.2.3.0x01:443", "0x7f.0.0.1:443",
            "127.0.0.01:443", "127.1:443"
        })
            Assert(numericAlias.Allows(ScopedPermissionPolicy.Kind.Net, alias), false,
                "numeric IPv4 alias under broad network allow");
        Assert(numericAlias.Allows(ScopedPermissionPolicy.Kind.Net,
            "127.0.0.1:443"), false, "canonical IPv4 explicit deny");
        Assert(numericAlias.Allows(ScopedPermissionPolicy.Kind.Net,
            "api.example.com:443"), true, "ordinary hostname still permitted");

        // Repeatable numeric/hex address matrix with broad network allow.
        // The host I/O connector must independently check resolved IPs.
        var numericWildcard = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "*") });
        var numericParts = new[] {
            "0", "00", "01", "1", "127", "0x0", "0x1", "0x7f",
            "0X7F", "0xFF", "077", "255", "256"
        };
        static bool CanonicalOctet(string s) =>
            s is "0" or "1" or "127" or "255";
        foreach (var first in numericParts)
        foreach (var second in numericParts)
        foreach (var third in numericParts)
        {
            var endpoint = first + "." + second + "." + third + ".1:443";
            Assert(numericWildcard.Allows(ScopedPermissionPolicy.Kind.Net, endpoint),
                CanonicalOctet(first) && CanonicalOctet(second) && CanonicalOctet(third),
                "noncanonical numeric IPv4 matrix");
        }

        var suffix = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "*.example.com:443") });
        Assert(suffix.Allows(ScopedPermissionPolicy.Kind.Net, "a.example.com:443"), true, "subdomain");
        Assert(suffix.Allows(ScopedPermissionPolicy.Kind.Net, "example.com:443"), false, "apex excluded");
        Assert(suffix.Allows(ScopedPermissionPolicy.Kind.Net, "badexample.com:443"), false, "suffix boundary");

        var absent = ScopedPermissionPolicy.FromHost(
            Array.Empty<ScopedPermissionPolicy.Kind>(),
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "*") });
        Assert(absent.Allows(ScopedPermissionPolicy.Kind.Net, "api.example.com:443"), false, "coarse ceiling");
        var denied = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] {
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "*"),
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "api.example.com:443", true)
            });
        Assert(denied.Allows(ScopedPermissionPolicy.Kind.Net, "api.example.com:443"), false, "network deny");
        Assert(denied.Allows(ScopedPermissionPolicy.Kind.Net, "api.example.com"), false, "portless denial");

        foreach (var invalid in new[] { "/workspace/../secret", "/workspace//secret", "relative/file" })
        {
            bool caught = false;
            try
            {
                _ = ScopedPermissionPolicy.FromHost(
                    new[] { ScopedPermissionPolicy.Kind.Read },
                    new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Read, invalid) });
            }
            catch (ArgumentException) { caught = true; }
            if (!caught) throw new Exception("malformed path permission admitted: " + invalid);
        }
        bool badEnum = false;
        try
        {
            _ = ScopedPermissionPolicy.FromHost(new[] { (ScopedPermissionPolicy.Kind)99 },
                Array.Empty<ScopedPermissionPolicy.Rule>());
        }
        catch (ArgumentException) { badEnum = true; }
        if (!badEnum) throw new Exception("unknown capability admitted");
        // Full v1 parity is NOT proven: no production guest host-I/O boundary
        // and no binary IPv6 address matcher in this bootstrap.
        Console.WriteLine("NET_SCOPED_PERMISSION_POLICY_SUBSTRATE_PASS");
    }
}
