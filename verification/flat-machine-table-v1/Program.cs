using Atto.MachineInspection;
using System.Globalization;

int checks = 0;
void Check(bool ok) { if (!ok) throw new Exception("check failed"); ++checks; }
void Reject(Action action)
{
    try { action(); }
    catch (ArgumentException e) when (e.Message == "E_MACHINE_TABLE") { ++checks; return; }
    throw new Exception("accepted invalid table");
}
Node N(string name, bool terminal, params Edge[] edges) => new(name, terminal, edges);
Edge E(string name, int target) => new(name, target);
Node[] Graph() => [N("Alpha", true), N("Zed", false, E("AlphaEvent", 0), E("Zulu", 1))];
string Id(string prefix, int i) => prefix + (1000 + i).ToString(CultureInfo.InvariantCulture);

var graph = Graph();
var table = FlatMachineTable.Verify(1, graph);
Check(table.Initial == 1);
Check(table.Names.SequenceEqual(["Alpha", "Zed"]));
Check(table.Events.SequenceEqual(["AlphaEvent", "Zulu"]));
Check(table.Successor(1, 0) == 0);
Check(table.Successor(1, 1) == 1);
Check(table.Successor(0, 0) is null);
Reject(() => table.Successor(-1, 0));
Reject(() => table.Successor(2, 0));
Reject(() => table.Successor(1, -1));
Reject(() => table.Successor(1, 2));
Reject(() => FlatMachineTable.Verify(2, graph));
Reject(() => FlatMachineTable.Verify(0, []));
Reject(() => FlatMachineTable.Verify(1, [graph[1], graph[0]]));
Reject(() => FlatMachineTable.Verify(1, [N("Alpha", true, E("X", 1)), graph[1]]));
Reject(() => FlatMachineTable.Verify(1, [graph[0], N("Zed", false, E("Zulu", 1), E("AlphaEvent", 0))]));
Reject(() => FlatMachineTable.Verify(1, [graph[0], N("Zed", false, E("AlphaEvent", 9))]));
Reject(() => FlatMachineTable.Verify(1, [graph[0], N("Zed", false, E("Zulu", 1))]));
Reject(() => FlatMachineTable.Verify(0, [N("A", false, E("E", 0), E("E", 0))]));
foreach (var invalid in new[] {"", "é", "9name", "A\n", new string('a', 1025)})
    Reject(() => FlatMachineTable.Verify(0, [N(invalid, true)]));
var edges = Enumerable.Range(0, 256).Select(i => E(Id("E", i), 0)).ToArray();
Check(FlatMachineTable.Verify(0, [N("N", false, edges)]).Events.Count == 256);
Reject(() => FlatMachineTable.Verify(0, [N("N", false, [..edges, E("E1256", 0)])]));
var nodes = Enumerable.Range(0, 256).Select(i => N(Id("N", i), i == 255,
    i == 255 ? [] : [E("Next", i + 1)])).ToArray();
Check(FlatMachineTable.Verify(0, nodes).Names.Count == 256);
Reject(() => FlatMachineTable.Verify(0, [..nodes, N("N1256", true)]));
var mutableEdges = new[] { E("Loop", 0) };
var detached = FlatMachineTable.Verify(0, [N("N", false, mutableEdges)]);
mutableEdges[0] = E("Loop", 99);
Check(detached.Successor(0, 0) == 0);
Console.WriteLine($"{checks} NativeAOT host graph-table checks passed (not guest execution)");
