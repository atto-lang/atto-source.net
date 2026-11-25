using System;
using System.Collections.Generic;
using System.Linq;

namespace Ores.Net.Compiler;

// Compile-time graph only; callers must authenticate the export inventory.
// No acquisition, manifest parser, runtime namespace object or privilege grant.
public sealed class StaticImportGraph
{
    private const int Limit = 256;
    public enum Kind { Namespace, Module, Fnc, Const, Class, Actor, Enum, Trait, Interface, Type }
    public enum Tier { Implicit, Deliberate, Explicit }
    public enum Domain { Builtin, Local, Package }
    public sealed record Origin
    {
        public Domain Domain { get; }
        public string Identity { get; }
        public string Digest { get; }
        public Origin(Domain domain, string identity, string digest)
        {
            if (domain is not (Domain.Builtin or Domain.Local or Domain.Package)
                || string.IsNullOrWhiteSpace(identity) || identity.Length > 256
                || digest is null || digest.Length != 64
                || digest.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw Error("E_IMPORT_PATH", "invalid pinned origin");
            Domain = domain; Identity = identity; Digest = digest;
        }
    }
    public sealed record Export(string Path, Kind Kind, bool Visible);
    public sealed record Selection(string Path, string Binding, Kind Kind, bool Explicit);
    public sealed record Alias(string Name, string Reference);
    public sealed record Target(View View, string Path, Kind Kind);
    private sealed record Binding(string Name, Tier Tier, Target Target);
    private sealed record Resolved(Target Target, string Remainder);

    public sealed class View
    {
        public Origin Origin { get; }
        private readonly Dictionary<string, Export> exports = new(StringComparer.Ordinal);
        public View(Origin origin, IReadOnlyList<Export> inventory)
        {
            Origin = origin ?? throw new ArgumentNullException(nameof(origin));
            if (inventory.Count > Limit) throw Error("E_SOURCE_LIMIT", "export budget exceeded");
            foreach (Export e in inventory)
            {
                RequirePath(e.Path);
                if (!ValidKind(e.Kind)) throw Error("E_IMPORT_KIND", "invalid export kind");
                if (!exports.TryAdd(e.Path, e)) throw Error("E_DUPLICATE", "duplicate export");
            }
        }
        public Target Lookup(string path)
        {
            RequirePath(path);
            if (!exports.TryGetValue(path, out Export? leaf)) throw Error("E_UNBOUND", "unknown export");
            if (!leaf.Visible) throw Error("E_PRIVATE", "private export");
            for (int dot = path.IndexOf('.'); dot >= 0; dot = path.IndexOf('.',dot+1))
            {
                if (!exports.TryGetValue(path[..dot], out Export? parent)) throw Error("E_UNBOUND", "missing parent namespace");
                if (!parent.Visible) throw Error("E_PRIVATE", "private parent namespace");
                if (!NamespaceKind(parent.Kind)) throw Error("E_IMPORT_KIND", "non-namespace parent");
            }
            return new Target(this, path, leaf.Kind);
        }
    }

    private List<Binding> bindings = new();
    private static FormatException Error(string code, string detail) => new(code + ": " + detail);
    private static void RequirePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 519) throw Error("E_IMPORT_PATH", "invalid static path");
        string[] parts = path.Split('.');
        if (parts.Length > 8 || parts.Any(p => !ContextualImportPath.Valid(p)))
            throw Error("E_IMPORT_PATH", "invalid static path");
    }
    private static bool NamespaceKind(Kind kind) => kind is Kind.Namespace or Kind.Module;
    private static bool ValidKind(Kind kind) => kind is Kind.Namespace or Kind.Module or Kind.Fnc or Kind.Const
        or Kind.Class or Kind.Actor or Kind.Enum or Kind.Trait or Kind.Interface or Kind.Type;
    private static bool Prefix(string a, string b) => b.StartsWith(a + ".", StringComparison.Ordinal);
    private static void Insert(List<Binding> staged, Binding b)
    {
        RequirePath(b.Name);
        if (staged.Count >= Limit) throw Error("E_SOURCE_LIMIT", "binding budget exceeded");
        if (b.Tier is not (Tier.Implicit or Tier.Deliberate or Tier.Explicit)) throw Error("E_IMPORT_KIND", "invalid tier");
        foreach (Binding prior in staged)
        {
            if (!ReferenceEquals(prior.Target.View,b.Target.View) && prior.Target.View.Origin == b.Target.View.Origin)
                throw Error("E_AMBIGUOUS", "multiple views for pinned origin");
            if (prior.Tier == Tier.Explicit && b.Tier == Tier.Explicit && prior.Name == b.Name)
                throw Error("E_DUPLICATE", "duplicate explicit binding");
            if ((prior.Tier == b.Tier || prior.Tier == Tier.Explicit || b.Tier == Tier.Explicit)
                && ((!NamespaceKind(prior.Target.Kind) && Prefix(prior.Name,b.Name))
                    || (!NamespaceKind(b.Target.Kind) && Prefix(b.Name,prior.Name))))
                throw Error("E_DUPLICATE", "non-namespace obstructs binding prefix");
        }
        staged.Add(b);
    }
    public void BindRoot(View view, string binding, Tier tier)
    {
        var staged = new List<Binding>(bindings);
        Insert(staged, new Binding(binding,tier,new Target(view,"",Kind.Namespace)));
        bindings = staged;
    }
    public void BindGroup(Origin requested, View view, IReadOnlyList<Selection> selections)
    {
        if (view.Origin != requested) throw Error("E_IMPORT_PATH", "wrong source identity");
        if (selections.Count == 0 || selections.Count > Limit - bindings.Count) throw Error("E_SOURCE_LIMIT", "invalid selection count");
        var staged = new List<Binding>(bindings);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Selection s in selections)
        {
            if (!seen.Add(s.Binding)) throw Error("E_DUPLICATE", "duplicate selection");
            Target t = view.Lookup(s.Path);
            if (t.Kind != s.Kind) throw Error("E_IMPORT_KIND", "wrong export kind");
            if (s.Binding.Contains('.') && !NamespaceKind(t.Kind)) throw Error("E_IMPORT_KIND", "qualified alias requires namespace");
            Insert(staged,new Binding(s.Binding,s.Explicit ? Tier.Explicit : Tier.Deliberate,t));
        }
        bindings = staged;
    }
    private static Target Resolve(List<Binding> scope, string reference)
    {
        RequirePath(reference);
        Tier? tier = null;
        Resolved? chosen = null;
        bool ambiguous = false;
        foreach (Binding b in scope)
        {
            if (b.Name != reference && !Prefix(b.Name,reference)) continue;
            var candidate = new Resolved(b.Target,b.Name == reference ? "" : reference[(b.Name.Length+1)..]);
            if (tier is null || (int)b.Tier > (int)tier.Value)
            {
                tier = b.Tier; chosen = candidate; ambiguous = false;
            }
            else if (b.Tier == tier && chosen != candidate) ambiguous = true;
        }
        if (chosen is null) throw Error("E_UNBOUND","unbound static reference");
        if (ambiguous) throw Error("E_AMBIGUOUS","ambiguous static prefix");
        Target t = chosen.Target;
        if (chosen.Remainder.Length == 0) return t;
        if (!NamespaceKind(t.Kind)) throw Error("E_IMPORT_KIND","non-namespace prefix");
        return t.View.Lookup(t.Path.Length == 0 ? chosen.Remainder : t.Path + "." + chosen.Remainder);
    }
    public Target Resolve(string reference) => Resolve(bindings,reference);

    private static SortedDictionary<string,string> Inventory(IReadOnlyList<Alias> aliases)
    {
        if (aliases.Count > Limit) throw Error("E_SOURCE_LIMIT","alias budget exceeded");
        var pending = new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach (Alias a in aliases)
        {
            if (a.Name is null || !ContextualImportPath.Valid(a.Name)) throw Error("E_IMPORT_PATH","invalid alias");
            RequirePath(a.Reference);
            if (!pending.TryAdd(a.Name,a.Reference)) throw Error("E_DUPLICATE","duplicate alias");
        }
        var checkedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string start in pending.Keys)
        {
            var walk = new List<string>();
            var positions = new Dictionary<string,int>(StringComparer.Ordinal);
            string current = start;
            while (pending.TryGetValue(current,out string? reference) && !checkedNames.Contains(current))
            {
                if (positions.TryGetValue(current,out int at))
                {
                    var cycle = walk.GetRange(at,walk.Count-at);
                    cycle.Add(current);
                    throw Error("E_IMPORT_CYCLE",string.Join(" -> ",cycle));
                }
                positions.Add(current,walk.Count);
                walk.Add(current);
                current = Root(reference);
            }
            checkedNames.UnionWith(walk);
        }
        return pending;
    }
    private static string Root(string path) { int dot = path.IndexOf('.'); return dot < 0 ? path : path[..dot]; }
    public static void PrecheckAliases(IReadOnlyList<Alias> aliases) => _ = Inventory(aliases);
    public void BindAliases(IReadOnlyList<Alias> aliases)
    {
        var pending = Inventory(aliases);
        if (pending.Count > Limit - bindings.Count) throw Error("E_SOURCE_LIMIT","binding budget exceeded");
        var staged = new List<Binding>(bindings);
        while (pending.Count != 0)
        {
            var ready = new List<string>();
            foreach (var e in pending)
            {
                if (pending.ContainsKey(Root(e.Value))) continue;
                Target t = Resolve(staged,e.Value);
                if (!NamespaceKind(t.Kind)) throw Error("E_IMPORT_KIND","alias requires namespace");
                Insert(staged,new Binding(e.Key,Tier.Explicit,t));
                ready.Add(e.Key);
            }
            if (ready.Count == 0) throw Error("E_IMPORT_CYCLE","cyclic namespace aliases");
            foreach (string name in ready) pending.Remove(name);
        }
        bindings = staged;
    }
}
