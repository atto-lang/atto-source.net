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
