using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Ores.Net.Runtime;

// Real managed .NET host test: not an AttoLang guest or linked compiler proof.
int count = 0;
void Check(bool ok, string message)
{
    count++;
    if (!ok) throw new Exception("singleton .NET host assertion failed: " + message);
}

void Reject<T>(Action action, string expected) where T : Exception
{
    count++;
    try { action(); }
    catch (T ex)
    {
        if (ex.Message.Contains(expected, StringComparison.Ordinal)) return;
        throw new Exception("wrong rejection message " + expected + ": " + ex.Message);
    }
    throw new Exception("expected rejection: " + expected);
}

var host = new SingletonDomainSnapshotV1(maxSingletons: 5, maxSymbols: 3);
var key = new SingletonDomainSnapshotV1.Key("tenant","pkg","Counter");
var data = new Dictionary<string, object?>
{
    ["count"] = 42L,
    ["nested"] = new Dictionary<string, object?> {["enabled"] = true},
    ["list"] = new List<object?> { 1L, "ok" },
};
object first = host.PublishConst(key, data);
object second = host.PublishConst(key, new Dictionary<string, object?>
{
    ["list"] = new List<object?> { 1L, "ok" },
    ["nested"] = new Dictionary<string, object?> {["enabled"] = true},
    ["count"] = 42L,
});
Check(ReferenceEquals(first,second), "canonical snapshot reused");
data["count"] = 900L;
var map = (IReadOnlyDictionary<string, object?>)first;
Check((long)map["count"]! == 42L, "source detached");
Check((bool)((IReadOnlyDictionary<string, object?>)map["nested"]!)["enabled"]!,
    "nested immutable");
Reject<NotSupportedException>(() =>
    ((IDictionary<string, object?>)map)["count"] = 99L,
    "read-only");
Reject<NotSupportedException>(() =>
    ((IList<object?>)map["list"]!)[0] = 44L,
    "read-only");
Reject<InvalidOperationException>(() => host.PublishConst(key,
    new Dictionary<string, object?> {["count"] = 43L}), "schema mismatch");
Check(ReferenceEquals(first,host.PublishConst(key,
    new Dictionary<string, object?>
    {
        ["nested"] = new Dictionary<string, object?> {["enabled"] = true},
        ["list"] = new List<object?> {1L,"ok"},
        ["count"] = 42L,
    })), "schema mismatch never overwrites");
var separateTenant = host.PublishConst(
    new SingletonDomainSnapshotV1.Key("another-tenant","pkg","Counter"),
    new Dictionary<string, object?> {["count"] = 1L});
Check(!ReferenceEquals(first,separateTenant), "two tenants cannot alias");
var otherDomain = new SingletonDomainSnapshotV1();
Check(!ReferenceEquals(first,otherDomain.PublishConst(
    key,new Dictionary<string, object?> {["count"] = 42L})),
    "separate domain identity");
Reject<NotSupportedException>(() => host.OpenMutableRegion(),
    "ATTO-SINGLETON-MUTABLE-GUEST-UNSUPPORTED");
Reject<ArgumentException>(() => host.PublishConst(
    new SingletonDomainSnapshotV1.Key("tenant/../bad","pkg","Counter"),
    new Dictionary<string, object?>()), "invalid");
Reject<ArgumentException>(() => host.PublishConst(
    new SingletonDomainSnapshotV1.Key("tenant","pkg","Effect"),
    new Dictionary<string, object?> {["mutable"] = new object()}), "unproved");
Reject<ArgumentException>(() => host.PublishConst(
    new SingletonDomainSnapshotV1.Key("tenant","pkg","NaN"),
    new Dictionary<string, object?> {["bad"] = double.NaN}), "nonfinite");

var cyclic = new Dictionary<string, object?>();
cyclic["self"] = cyclic;
Reject<ArgumentException>(() => host.PublishConst(
    new SingletonDomainSnapshotV1.Key("tenant","pkg","Cycle"), cyclic),
    "cyclic");
var catalog = host.InternSymbol("process","#ready");
Check(ReferenceEquals(catalog,host.InternSymbol("process","#ready")),"canonical Symbol instance");
Check(host.ResolveSymbol("process","#ready") == catalog,"nominal descriptor decode");
Check(!catalog.Equals("#ready"),"Symbol is not string");
Reject<UnauthorizedAccessException>(() => host.ResolveSymbol("process","#unknown"),
    "unknown symbol");
var privateSym = host.InternSymbol("private","#secret");
Reject<UnauthorizedAccessException>(() => host.ResolveSymbol("private","#secret"),
    "private symbol transport denied");
Check(ReferenceEquals(privateSym,host.ResolveSymbol("private","#secret",true)),
    "trusted private decode");
host.InternSymbol("core","#core");
Reject<InvalidOperationException>(() => host.InternSymbol("process","#overflow"),
    "symbol quota");

var concurrent = new SingletonDomainSnapshotV1();
var concurrentKey = new SingletonDomainSnapshotV1.Key("tenant","pkg","Parallel");
var outputs = await Task.WhenAll(Enumerable.Range(0,64).Select(_ => Task.Run(
    () => concurrent.PublishConst(concurrentKey,
        new Dictionary<string, object?> {["i"] = 25L}))));
foreach (object output in outputs)
    Check(ReferenceEquals(outputs[0],output), "64 physical .NET parallel calls same identity");

Console.WriteLine("NET_SINGLETON_HOST_ASSERTIONS=" + count);
Console.WriteLine("NET_SINGLETON_HOST_PASS=true");
Console.WriteLine("NET_COMPILED_GUEST=NOT_EXECUTED");
Console.WriteLine("NET_SHARED_RW_REGION=NOT_IMPLEMENTED");
