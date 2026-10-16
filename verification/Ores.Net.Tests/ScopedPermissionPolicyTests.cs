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
