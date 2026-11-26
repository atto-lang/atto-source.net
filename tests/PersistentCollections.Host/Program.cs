// Model-checked tests for Ores.Net.Runtime.Persistent. Every operation is
// compared against a mutable BCL model, and retained snapshots must still
// equal their model copies at the end (persistence). Deterministic seed.
using Ores.Net.Runtime.Persistent;

var failures = 0;
void Check(bool ok, string what)
{
    if (!ok) { failures++; Console.Error.WriteLine($"FAIL {what}"); }
}

ulong state = 0x9E3779B97F4A7C15UL;
int Next(int bound)
{
    state ^= state << 13; state ^= state >> 7; state ^= state << 17;
    return (int)(state % (ulong)bound);
}

// ---- Map: random ops vs Dictionary, with snapshots ----
void MapModel<TKey>(string name, PersistentMap<TKey, int> start, Func<int, TKey> key, int ops, int keySpace) where TKey : notnull
{
    var map = start;
    var model = new Dictionary<TKey, int>(start.Count);
    var snaps = new List<(PersistentMap<TKey, int>, Dictionary<TKey, int>)>();
    for (var i = 0; i < ops; i++)
    {
        var k = key(Next(keySpace));
        if (Next(3) == 0) { map = map.Remove(k); model.Remove(k); }
        else { var v = Next(1000); map = map.SetItem(k, v); model[k] = v; }
        if (map.Count != model.Count) { Check(false, $"{name} count at op {i}"); return; }
        var probe = key(Next(keySpace));
        var hit = map.TryGetValue(probe, out var got);
        Check(hit == model.TryGetValue(probe, out var want) && (!hit || got == want), $"{name} lookup at op {i}");
        if (i % 997 == 0) snaps.Add((map, new Dictionary<TKey, int>(model)));
    }
    Check(map.Count() == model.Count, $"{name} enumeration count");
    foreach (var (snap, copy) in snaps)
    {
        Check(snap.Count == copy.Count, $"{name} snapshot count");
        foreach (var (k, v) in copy) Check(snap.TryGetValue(k, out var s) && s == v, $"{name} snapshot value");
    }
    Console.WriteLine($"PASS map/{name} ops={ops} snapshots={snaps.Count} final={map.Count}");
}

MapModel("int", PersistentMap<int, int>.Empty, k => k, 30000, 5000);
MapModel("string", PersistentMap<string, int>.Empty, k => "k" + k, 20000, 3000);
// Degenerate comparer: only 4 distinct hash codes, so collision nodes are exercised.
MapModel("colliding", PersistentMap<int, int>.WithComparer(new FourBuckets()), k => k, 4000, 300);
// Hashes that share long prefixes stress deep bitmap splits.
MapModel("deep", PersistentMap<int, int>.WithComparer(new HighBitsOnly()), k => k, 6000, 2000);

{
    var a = PersistentMap<string, int>.Empty.SetItem("x", 1);
    Check(ReferenceEquals(a, a.SetItem("x", 1)), "no-op SetItem returns same instance");
    Check(ReferenceEquals(a, a.Remove("missing")), "no-op Remove returns same instance");
    var b = PersistentMap<string, int>.Empty.SetItem("y", 2).SetItem("x", 1).Remove("y");
    Check(a.ContentEquals(b), "content equality is order independent");
    Check(PersistentMap<string, int>.Empty.SetItem("x", 1).Remove("x").Count == 0, "remove to empty");
    Console.WriteLine("PASS map/identity");
}

// ---- Vector: grow past 3 trie levels, random updates, shrink to empty ----
{
    const int n = 40000; // > 32^3 = 32768, forces shift 15
    var v = PersistentVector<int>.Empty;
    var model = new List<int>();
    var snaps = new List<(PersistentVector<int>, int[])>();
    for (var i = 0; i < n; i++)
    {
        v = v.Add(i * 3);
        model.Add(i * 3);
        if (i is 31 or 32 or 1055 or 1056 or 32799 or 32800 || i % 4999 == 0) snaps.Add((v, model.ToArray()));
    }
    Check(v.Count == n && v[n - 1] == (n - 1) * 3 && v[0] == 0 && v[32768] == 32768 * 3, "vector append/index");
    for (var i = 0; i < 20000; i++)
    {
        var idx = Next(n);
        var val = Next(1_000_000);
        v = v.SetItem(idx, val);
        model[idx] = val;
    }
    Check(v.SequenceEqual(model), "vector after random SetItem");
    snaps.Add((v, model.ToArray()));
    while (v.Count > 0)
    {
        v = v.RemoveLast();
        model.RemoveAt(model.Count - 1);
        if (v.Count is 32800 or 32768 or 1056 or 1024 or 33 or 32 or 1)
            Check(v.SequenceEqual(model), $"vector after RemoveLast to {v.Count}");
    }
    Check(v.Count == 0, "vector drained");
    foreach (var (snap, copy) in snaps) Check(snap.SequenceEqual(copy), $"vector snapshot len={copy.Length}");
    // Regrow after draining reuses the empty shape correctly.
    v = PersistentVector<int>.From(Enumerable.Range(0, 2000));
    Check(v.SequenceEqual(Enumerable.Range(0, 2000)), "vector regrow");
    var threw = false;
    try { _ = v[2000]; } catch (ArgumentOutOfRangeException) { threw = true; }
    Check(threw, "vector bounds check");
    Console.WriteLine($"PASS vector n={n} snapshots={snaps.Count}");
}

Console.WriteLine(failures == 0 ? "PERSISTENT_COLLECTIONS|OK" : $"PERSISTENT_COLLECTIONS|FAIL|{failures}");
return failures == 0 ? 0 : 1;

sealed class FourBuckets : IEqualityComparer<int>
{
    public bool Equals(int x, int y) => x == y;
    public int GetHashCode(int obj) => obj & 3;
}

sealed class HighBitsOnly : IEqualityComparer<int>
{
    public bool Equals(int x, int y) => x == y;
    // Low 25 bits identical; keys differ only in the top bits.
    public int GetHashCode(int obj) => (obj << 25) | 0x01ABCDEF & 0x01FFFFFF;
}
