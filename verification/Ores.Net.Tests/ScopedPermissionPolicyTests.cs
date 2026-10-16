using Ores.Net.Runtime;
using System.Net;
using System.Net.Sockets;

internal static class ScopedPermissionPolicyTests
{
    private static void Assert(bool got, bool expected, string name)
    {
        if (got != expected) throw new Exception("Scoped permission policy: " + name);
    }


    // Actual .NET policy-evaluator checks against the 37 canonical v1
    // authorization vectors, not guest OS or host-syscall enforcement proof.
    private sealed record GoldenAuthorization(string Id, ScopedPermissionPolicy.Kind Permission,
        string Resource, bool Coarse, string[] Grants, string[] Denials, bool Expected);

    private static void RunCanonicalAuthorizationVectors()
    {
        // The test data is generated from the canonical JSON. Do not silently
        // weaken this oracle by changing only a generated row.
        var corpus = System.IO.File.ReadAllBytes(
            "contracts/v1/scoped-permissions-v1.json");
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(corpus))
            .ToLowerInvariant();
        if (digest != "b481b72d9e5fdb2c1a4d4de7745a02f7ec3af8a937b0f067885d184efc3cb308")
            throw new Exception("canonical scoped-permission corpus changed; review regenerated vectors");
        GoldenAuthorization[] vectors =
        {
            new("default-read-denied", ScopedPermissionPolicy.Kind.Read, "/workspace/input.txt", false, new string[] {}, new string[] {}, false),
            new("default-write-denied", ScopedPermissionPolicy.Kind.Write, "/workspace/out.txt", false, new string[] {}, new string[] {}, false),
            new("default-net-denied", ScopedPermissionPolicy.Kind.Net, "api.example.com:443", false, new string[] {}, new string[] {}, false),
            new("default-env-denied", ScopedPermissionPolicy.Kind.Env, "API_KEY", false, new string[] {}, new string[] {}, false),
            new("default-run-denied", ScopedPermissionPolicy.Kind.Run, "git", false, new string[] {}, new string[] {}, false),
            new("default-sys-denied", ScopedPermissionPolicy.Kind.Sys, "os", false, new string[] {}, new string[] {}, false),
            new("default-ffi-denied", ScopedPermissionPolicy.Kind.Ffi, "/workspace/libplugin.so", false, new string[] {}, new string[] {}, false),
            new("default-import-denied", ScopedPermissionPolicy.Kind.Import, "/workspace/import.ores", false, new string[] {}, new string[] {}, false),
            new("coarse-ceiling-net", ScopedPermissionPolicy.Kind.Net, "api.example.com:443", false, new string[] {"*"}, new string[] {}, false),
            new("coarse-ceiling-env", ScopedPermissionPolicy.Kind.Env, "API_KEY", false, new string[] {"*"}, new string[] {}, false),
            new("explicit-net-grant", ScopedPermissionPolicy.Kind.Net, "api.example.com:443", true, new string[] {"api.example.com:443"}, new string[] {}, true),
            new("explicit-env-grant", ScopedPermissionPolicy.Kind.Env, "API_KEY", true, new string[] {"API_KEY"}, new string[] {}, true),
            new("explicit-run-grant", ScopedPermissionPolicy.Kind.Run, "git", true, new string[] {"git"}, new string[] {}, true),
            new("deny-wins-net", ScopedPermissionPolicy.Kind.Net, "api.example.com:443", true, new string[] {"*"}, new string[] {"api.example.com:443"}, false),
            new("deny-wins-env", ScopedPermissionPolicy.Kind.Env, "API_KEY", true, new string[] {"*"}, new string[] {"API_KEY"}, false),
            new("deny-all-wins", ScopedPermissionPolicy.Kind.Net, "other.example.org:443", true, new string[] {"*"}, new string[] {"*"}, false),
            new("endpoint-port-separation", ScopedPermissionPolicy.Kind.Net, "api.example.com:444", true, new string[] {"api.example.com:443"}, new string[] {}, false),
            new("endpoint-upper-normalized", ScopedPermissionPolicy.Kind.Net, "API.EXAMPLE.COM.:0443", true, new string[] {"api.example.com:443"}, new string[] {}, true),
            new("endpoint-deny-fqdn", ScopedPermissionPolicy.Kind.Net, "API.EXAMPLE.COM.:443", true, new string[] {"*"}, new string[] {"api.example.com:443"}, false),
            new("endpoint-subdomain-yes", ScopedPermissionPolicy.Kind.Net, "a.example.com:443", true, new string[] {"*.example.com:443"}, new string[] {}, true),
            new("endpoint-subdomain-no-apex", ScopedPermissionPolicy.Kind.Net, "example.com:443", true, new string[] {"*.example.com:443"}, new string[] {}, false),
            new("endpoint-subdomain-no-suffix", ScopedPermissionPolicy.Kind.Net, "badexample.com:443", true, new string[] {"*.example.com:443"}, new string[] {}, false),
            new("endpoint-deny-portless", ScopedPermissionPolicy.Kind.Net, "api.example.com", true, new string[] {"*"}, new string[] {"api.example.com:443"}, false),
            new("endpoint-deny-wildcard-port", ScopedPermissionPolicy.Kind.Net, "a.example.com:443", true, new string[] {"*"}, new string[] {"*.example.com:443"}, false),
            new("endpoint-ipv6-short", ScopedPermissionPolicy.Kind.Net, "[::1]:443", true, new string[] {"[0:0:0:0:0:0:0:1]:443"}, new string[] {}, true),
            new("endpoint-ipv6-deny", ScopedPermissionPolicy.Kind.Net, "[0:0:0:0:0:0:0:1]:443", true, new string[] {"*"}, new string[] {"[::1]:443"}, false),
            new("endpoint-ipv4-alias", ScopedPermissionPolicy.Kind.Net, "127.1:443", true, new string[] {"*"}, new string[] {"127.0.0.1"}, false),
            new("endpoint-ipv4-hex-alias", ScopedPermissionPolicy.Kind.Net, "0x7f.0.0.1:443", true, new string[] {"*"}, new string[] {"127.0.0.1"}, false),
            new("endpoint-invalid-port", ScopedPermissionPolicy.Kind.Net, "api.example.com:0", true, new string[] {"*"}, new string[] {}, false),
            new("endpoint-invalid-credentials", ScopedPermissionPolicy.Kind.Net, "alice@api.example.com:443", true, new string[] {"*"}, new string[] {}, false),
            new("env-whitespace-exact", ScopedPermissionPolicy.Kind.Env, "API_KEY ", true, new string[] {"API_KEY"}, new string[] {}, false),
            new("env-case-exact", ScopedPermissionPolicy.Kind.Env, "api_key", true, new string[] {"API_KEY"}, new string[] {}, false),
            new("run-prefix", ScopedPermissionPolicy.Kind.Run, "git-status", true, new string[] {"git*"}, new string[] {}, true),
            new("run-prefix-no-match", ScopedPermissionPolicy.Kind.Run, "curl", true, new string[] {"git*"}, new string[] {}, false),
            new("path-child-allowed", ScopedPermissionPolicy.Kind.Read, "/workspace/data/file.txt", true, new string[] {"/workspace/data"}, new string[] {}, true),
            new("path-prefix-no-match", ScopedPermissionPolicy.Kind.Read, "/workspace/database/file.txt", true, new string[] {"/workspace/data"}, new string[] {}, false),
            new("path-whitespace-exact", ScopedPermissionPolicy.Kind.Read, "/workspace/data ", true, new string[] {"/workspace/data"}, new string[] {}, false)
        };
        if (vectors.Length != 37)
            throw new Exception("reviewed scoped authorization corpus is incomplete");
        foreach (var vector in vectors)
        {
            var rules = new List<ScopedPermissionPolicy.Rule>();
            foreach (var grant in vector.Grants)
                rules.Add(new ScopedPermissionPolicy.Rule(vector.Permission, grant));
            foreach (var denial in vector.Denials)
                rules.Add(new ScopedPermissionPolicy.Rule(vector.Permission, denial, true));
            var ceiling = vector.Coarse
                ? new[] { vector.Permission }
                : Array.Empty<ScopedPermissionPolicy.Kind>();
            var policy = ScopedPermissionPolicy.FromHost(ceiling, rules);
            Assert(policy.Allows(vector.Permission, vector.Resource),
                vector.Expected, vector.Id);
        }
    }

    public static void Run()
    {
        RunCanonicalAuthorizationVectors();
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
        Assert(file.Allows(ScopedPermissionPolicy.Kind.Read,
            "/workspace/" + new string('x', 5000)), false, "oversized path denied");
        bool oversizedRejected = false;
        try
        {
            _ = ScopedPermissionPolicy.FromHost(
                new[] { ScopedPermissionPolicy.Kind.Env },
                Enumerable.Repeat(new ScopedPermissionPolicy.Rule(
                    ScopedPermissionPolicy.Kind.Env, "KEY"), 513));
        }
        catch (ArgumentException) { oversizedRejected = true; }
        if (!oversizedRejected) throw new Exception("oversized permission rule count admitted");
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
        // The native C++ reference previously wrapped 2^32+443 to 443.
        // Preserve the same fail-closed long-port rule in the .NET backend.
        foreach (var badPort in new[] { "4294967739", "4294967297", "000443", "9999999999" })
        {
            Assert(net.Allows(ScopedPermissionPolicy.Kind.Net,
                "api.example.com:" + badPort), false, "oversized DNS port rejected");
            bool invalidScope = false;
            try
            {
                _ = ScopedPermissionPolicy.FromHost(
                    new[] { ScopedPermissionPolicy.Kind.Net },
                    new[] { new ScopedPermissionPolicy.Rule(
                        ScopedPermissionPolicy.Kind.Net, "api.example.com:" + badPort) });
            }
            catch (ArgumentException) { invalidScope = true; }
            if (!invalidScope) throw new Exception("overlong DNS permission scope admitted");
        }
        Assert(net.Allows(ScopedPermissionPolicy.Kind.Net, "api.example.com:0443"),
            true, "zero-padded port of valid length remains accepted");
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
        foreach (var padded in new[] {
            " api.example.com:443", "api.example.com:443 ",
            " 127.0.0.1:443", "127.0.0.1:443 "
        })
        {
            Assert(numericWildcard.Allows(ScopedPermissionPolicy.Kind.Net, padded),
                false, "padded network resource must not acquire wildcard authority");
            bool rejected = false;
            try
            {
                _ = ScopedPermissionPolicy.FromHost(
                    new[] { ScopedPermissionPolicy.Kind.Net },
                    new[] { new ScopedPermissionPolicy.Rule(
                        ScopedPermissionPolicy.Kind.Net, padded) });
            }
            catch (ArgumentException) { rejected = true; }
            if (!rejected)
                throw new Exception("padded network rule was silently normalized: " + padded);
        }
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

        var ipv6 = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net,
                "[0:0:0:0:0:0:0:1]:443") });
        Assert(ipv6.Allows(ScopedPermissionPolicy.Kind.Net, "[::1]:443"),
            true, "compressed IPv6 numeric identity");
        Assert(ipv6.Allows(ScopedPermissionPolicy.Kind.Net, "[::1]:444"),
            false, "IPv6 port separation");
        foreach (var badPort in new[] { "4294967739", "4294967297", "000443", "9999999999" })
        {
            Assert(ipv6.Allows(ScopedPermissionPolicy.Kind.Net,
                "[::1]:" + badPort), false, "oversized IPv6 port rejected");
            bool invalidScope = false;
            try
            {
                _ = ScopedPermissionPolicy.FromHost(
                    new[] { ScopedPermissionPolicy.Kind.Net },
                    new[] { new ScopedPermissionPolicy.Rule(
                        ScopedPermissionPolicy.Kind.Net, "[::1]:" + badPort) });
            }
            catch (ArgumentException) { invalidScope = true; }
            if (!invalidScope) throw new Exception("overlong IPv6 permission scope admitted");
        }
        Assert(ipv6.Allows(ScopedPermissionPolicy.Kind.Net, "[::1]:0443"),
            true, "valid zero-padded IPv6 port remains accepted");
        var ipv6Deny = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] {
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "*"),
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net,
                    "[::1]:443", true)
            });
        Assert(ipv6Deny.Allows(ScopedPermissionPolicy.Kind.Net,
            "[0:0:0:0:0:0:0:1]:443"), false, "expanded IPv6 cannot bypass deny");
        Assert(ipv6Deny.Allows(ScopedPermissionPolicy.Kind.Net, "[::1]"),
            false, "portless IPv6 cannot bypass deny");
        foreach (var unsafeEndpoint in new[] {
            "::1:443", "[::1", "[::1]:", "[::1]:0", "[::1]:65536",
            "[::1]x:443", "[::1]:-1", "[fe80::1%eth0]:443",
            "[::ffff:127.0.0.1]:443", "[::192.0.2.1]:443",
            "[::ffff:c000:201]:443"
        })
            Assert(ipv6Deny.Allows(ScopedPermissionPolicy.Kind.Net, unsafeEndpoint),
                false, "unsafe IPv6 endpoint spelling");
        
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

        var named = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] {
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "api.example.com:443"),
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "127.0.0.1", true)
            });
        Assert(named.Allows(ScopedPermissionPolicy.Kind.Net, "api.example.com:443"),
            true, "hostname alone authorized before DNS");
        Assert(named.AllowsNetworkConnection("api.example.com:443", "127.0.0.1:443"),
            false, "resolved loopback denied");
        Assert(named.AllowsNetworkConnection("api.example.com:443", "203.0.113.7:443"),
            true, "unblocked canonical resolved IPv4");
        Assert(named.AllowsNetworkConnection("api.example.com:443", "203.0.113.7:444"),
            false, "selected port mismatch");
        Assert(named.AllowsNetworkConnection("api.example.com:443", "evil.example:443"),
            false, "selected result must be numeric");
        Assert(named.AllowsNetworkConnection("api.example.com:443", "127.1:443"),
            false, "numeric IP alias rejected");
        Assert(named.AllowsNetworkConnection("api.example.com:443", "[::1]:443"),
            false, "unsupported IPv6 fails closed");
        Assert(named.AllowsNetworkConnection("api.example.com", "203.0.113.7:443"),
            false, "pre-connect checks require requested port");
        Assert(named.AllowsNetworkConnection("other.example.com:443", "203.0.113.7:443"),
            false, "unpermitted hostname not rescued by safe resolved IP");

        var exactLiteral = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "203.0.113.7:443") });
        Assert(exactLiteral.AllowsNetworkConnection("203.0.113.7:443", "203.0.113.7:443"),
            true, "numeric literal bound to selected address");
        Assert(exactLiteral.AllowsNetworkConnection("203.0.113.7:443", "198.51.100.8:443"),
            false, "numeric literal may not authorize another address");
        var wildcardLiteral = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] { new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "*") });
        Assert(wildcardLiteral.AllowsNetworkConnection("203.0.113.7:443", "198.51.100.8:443"),
            false, "wildcard grant cannot bypass numeric identity");
        Assert(wildcardLiteral.AllowsNetworkConnection("[::1]:443", "203.0.113.7:443"),
            false, "IPv6 request must not use IPv4-only pre-connect policy");
        Assert(wildcardLiteral.AllowsNetworkConnection("api.example.com:443", "0.0.0.0:443"),
            false, "unspecified selected IPv4 must not alias to loopback");
        Assert(wildcardLiteral.AllowsNetworkConnection("0.0.0.0:443", "0.0.0.0:443"),
            false, "unspecified numeric request is not a remote destination");
        Assert(wildcardLiteral.AllowsNetworkConnection("203.0.113.7:443", "203.0.113.7:443"),
            true, "valid pinned numeric IPv4 remains allowed");

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
        // Exercise the actual trusted .NET socket adapter, not only the
        // substrate's AllowsNetworkConnection predicate.
        var selectedV4 = IPAddress.Parse("203.0.113.7");
        var selectedPolicy = ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net },
            new[] {
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "api.example.com:443"),
                new ScopedPermissionPolicy.Rule(ScopedPermissionPolicy.Kind.Net, "127.0.0.1", true)
            });
        var pinned = ScopedTcpConnector.AuthorizeSelectedAddress(
            selectedPolicy, "api.example.com:443", selectedV4, 443);
        if (!pinned.Address.Equals(selectedV4) || pinned.Port != 443)
            throw new Exception("selected .NET socket IP/port was not pinned");
        void MustDenySocket(string requested, IPAddress ip, int port, string name)
        {
            bool denied = false;
            try { _ = ScopedTcpConnector.AuthorizeSelectedAddress(selectedPolicy, requested, ip, port); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied) throw new Exception("unsafe selected IP admitted: " + name);
        }
        MustDenySocket("api.example.com:443", IPAddress.Loopback, 443, "resolved loopback deny");
        MustDenySocket("api.example.com:443", selectedV4, 444, "port mismatch");
        MustDenySocket("other.example.com:443", selectedV4, 443, "unauthorized requested host");
        MustDenySocket("api.example.com:443", IPAddress.IPv6Loopback, 443, "IPv6 fail closed");

        // A real local TCP loopback proves ConnectAsync uses the checked
        // numeric endpoint. No external networking or live DNS is required.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var localPolicy = ScopedPermissionPolicy.FromHost(
                new[] { ScopedPermissionPolicy.Kind.Net },
                new[] { new ScopedPermissionPolicy.Rule(
                    ScopedPermissionPolicy.Kind.Net, "127.0.0.1:" + port) });
            using (Socket client = ScopedTcpConnector.ConnectAsync(
                localPolicy, "127.0.0.1", port).GetAwaiter().GetResult())
            {
                if (!client.Connected ||
                    !client.RemoteEndPoint!.Equals(new IPEndPoint(IPAddress.Loopback, port)))
                    throw new Exception("socket did not connect to approved pinned loopback");
            }
            bool deniedConnect = false;
            try
            {
                _ = ScopedTcpConnector.ConnectAsync(
                    ScopedPermissionPolicy.DefaultDeny(), "127.0.0.1", port)
                    .GetAwaiter().GetResult();
            }
            catch (UnauthorizedAccessException) { deniedConnect = true; }
            if (!deniedConnect) throw new Exception("default deny allowed real TCP connection");
        }
        finally { listener.Stop(); }

        // Full v1 parity is NOT proven: no production guest host-I/O boundary
        // and no verified resolved-IPv6 connect adapter in this bootstrap.
        Console.WriteLine("NET_SCOPED_PERMISSION_POLICY_SUBSTRATE_PASS");
    }
}
