using Ores.Net.Compiler;
using System;
using System.Linq;
using static Ores.Net.Compiler.RuntimeParkingGraph;

static void Check(bool value)
{
    if (!value) throw new Exception("parking graph assertion failed");
}
static void Reject(Callable[] nodes)
{
    try { _ = Infer(nodes); }
    catch (FormatException) { return; }
    throw new Exception("invalid graph admitted");
}
RuntimeParkingEffects empty = default;
var park = new RuntimeParkingEffects(false, true, false);
var suspend = new RuntimeParkingEffects(true, false, false);
var rows = Infer(new Callable[] {
    new(empty, new int?[]{1}), new(empty, new int?[]{0,2}),
    new(park, Array.Empty<int?>()), new(empty, Array.Empty<int?>()),
    new(suspend, Array.Empty<int?>()), new(park, new int?[]{4}),
    new(empty, new int?[]{null}), new(empty, new int?[]{6})
});
foreach (var row in rows.Take(3))
    Check(row.RuntimePark && row.RequiresResumableFrame && !row.RequiresSuspendAnnotation && !row.SafeWithExclusiveLoan);
Check(rows[3].SafeWithExclusiveLoan);
Check(rows[5].ExplicitSuspend && rows[5].RuntimePark && rows[5].RequiresSuspendAnnotation);
Check(rows[7].Unresolved && rows[7].RequiresResumableFrame && !rows[7].SafeWithExclusiveLoan);
Reject(new Callable[]{new(empty,new int?[]{1})});
Reject(new Callable[]{new(empty,new int?[]{-1})});
Reject(new Callable[]{new(empty,new int?[1025])});
Reject(Enumerable.Range(0,1025).Select(_=>new Callable(empty,Array.Empty<int?>())).ToArray());
Reject(Enumerable.Range(0,17).Select(_=>new Callable(empty,new int?[1024])).ToArray());
var chain = Enumerable.Range(0,1024).Select(i => i==1023
    ? new Callable(park,new int?[]{0})
    : new Callable(empty,new int?[]{i+1})).ToArray();
Check(Infer(chain).All(row=>row.RuntimePark && !row.RequiresSuspendAnnotation));
Console.WriteLine("PASS: .NET parking graph recursive closure, annotation separation, unresolved/loan fences, invalid IDs and graph budgets");

var seed = 1127UL;
int Next(int modulus)
{
    seed = unchecked(seed * 6364136223846793005UL + 1UL);
    return (int)((seed >> 32) % (ulong)modulus);
}
for (int trial = 0; trial < 256; trial++)
{
    int n = 1 + Next(48);
    var graph = new Callable[n];
    for (int i = 0; i < n; i++)
    {
        int bits = Next(16);
        var direct = new RuntimeParkingEffects(bits == 1, bits == 2, bits == 3);
        var calls = new int?[Next(8)];
        for (int k = 0; k < calls.Length; k++)
        {
            int target = Next(n + 3);
            calls[k] = target < n ? target : null;
        }
        graph[i] = new Callable(direct, calls);
    }
    var actual = Infer(graph);
    for (int root = 0; root < n; root++)
    {
        var seen = new bool[n];
        var pending = new System.Collections.Generic.Stack<int>();
        pending.Push(root);
        bool susp = false, parking = false, unresolved = false;
        while (pending.Count != 0)
        {
            int i = pending.Pop();
            if (seen[i]) continue;
            seen[i] = true;
            susp |= graph[i].Direct.ExplicitSuspend;
            parking |= graph[i].Direct.RuntimePark;
            unresolved |= graph[i].Direct.Unresolved;
            foreach (var target in graph[i].Calls)
            {
                if (target.HasValue) pending.Push(target.Value);
                else unresolved = true;
            }
        }
        Check(actual[root] == new RuntimeParkingEffects(susp, parking, unresolved));
        Check(actual[root].RequiresSuspendAnnotation == susp);
    }
}
var exactBudget = Enumerable.Range(0, 1024).Select(i =>
    new Callable(i == 1023 ? park : empty,
        Enumerable.Repeat<int?>((i + 1) % 1024, 16).ToArray())).ToArray();
Check(Infer(exactBudget).All(row => row == park));
Console.WriteLine("PASS: 256 differential graph trials and exact 1024-node/16384-edge budget");
