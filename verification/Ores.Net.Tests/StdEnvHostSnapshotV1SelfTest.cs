using System;
using System.Collections.Generic;
using Ores.Net.Runtime;

internal static class StdEnvHostSnapshotV1SelfTest
{
    internal static void Run()
    {
        var source = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ATTO_EMPTY"] = "",
            ["ATTO_NAME"] = "original",
            ["ATTO_FLAG"] = "true",
            ["ATTO_SECRET"] = "never-expose"
        };
        var allow = new List<string> { "ATTO_*" };
        var deny = new List<string> { "ATTO_SECRET" };
        var hosted = StdEnvHostSnapshotV1.Create(source, allow, deny, true);
        source["ATTO_NAME"] = "replaced";
        allow.Add("*");
        deny.Clear();
        Require(hosted.Get("ATTO_NAME") == new StdEnvHostSnapshotV1.Lookup(true, "original"),
            "host copied allowed value");
        Require(hosted.Get("ATTO_EMPTY") == new StdEnvHostSnapshotV1.Lookup(true, ""),
            "present empty must not be considered absent");
        Require(hosted.Get("ATTO_MISSING") == new StdEnvHostSnapshotV1.Lookup(false, null),
            "missing granted key is absent");
        Require(hosted.Has("ATTO_EMPTY"), "empty value must be present");
        Require(!hosted.Has("ATTO_MISSING"), "missing key must not be present");
        Denied(() => hosted.Get("ATTO_SECRET"), "explicit deny after allow");
        Denied(() => hosted.Has("ATTO_SECRET"), "denied existence probe");
        Denied(() => hosted.Get("PATH"), "snapshot of broad policy incorrectly granted");
        Require(hosted.Revoke(), "first revoke must succeed");
        Require(!hosted.Revoke(), "second revoke must be idempotent");
        Denied(() => hosted.Get("ATTO_NAME"), "revoked read");
        Denied(() => hosted.Has("ATTO_NAME"), "revoked has");

        var noCoarse = StdEnvHostSnapshotV1.Create(source, new[] { "*" },
            Array.Empty<string>(), false);
        Denied(() => noCoarse.Get("ATTO_NAME"), "coarse capability omitted");

        Invalid(() => StdEnvHostSnapshotV1.Create(source, new[] { "ATTO_=" },
            Array.Empty<string>(), true), "invalid scope");
        Invalid(() => StdEnvHostSnapshotV1.Create(source, new[] { "*" },
            new[] { "ATTO_**" }, true), "invalid deny scope");
        Invalid(() => hosted.Get("ATTO-BAD"), "invalid key");
        Invalid(() => hosted.Get("1BAD"), "leading digit");

        var bad = new Dictionary<string, string> { ["ATTO_NAME"] = "é".PadLeft(16385, 'x') };
        Invalid(() => StdEnvHostSnapshotV1.Create(bad, new[] { "*" },
            Array.Empty<string>(), true), "UTF8 byte quota");
        bad["ATTO_NAME"] = new string((char)0xD800, 1);
        Invalid(() => StdEnvHostSnapshotV1.Create(bad, new[] { "*" },
            Array.Empty<string>(), true), "lone surrogate");
        bad["ATTO_NAME"] = new string('x', 16384);
        Require(StdEnvHostSnapshotV1.Create(bad, new[] { "*" },
            Array.Empty<string>(), true).Has("ATTO_NAME"), "exact byte limit");
    }

    private static void Require(bool value, string failure)
    {
        if (!value) throw new Exception("FAILED: " + failure);
    }

    private static void Denied(Action action, string label)
    {
        try { action(); }
        catch (UnauthorizedAccessException e)
        {
            Require(e.Message == "ENV_PERMISSION_DENIED", "secret-safe denial for " + label);
            return;
        }
        throw new Exception("FAILED: expected environment denial: " + label);
    }

    private static void Invalid(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException e)
        {
            Require(e.Message.StartsWith("ENV_INVALID_", StringComparison.Ordinal),
                "secret-safe invalid: " + label);
            return;
        }
        throw new Exception("FAILED: expected invalid environment input: " + label);
    }
}
