using System;
using System.Collections.Generic;
using System.Linq;

namespace Ores.Net.Compiler;

/// <summary>Experimental trusted-host one-entry exact frozen CIL session.
/// Package snapshots are immutable and must have been independently verified
/// by the host. Re-admit the whole dependency graph before every lookup.
/// No persisted artifact authority, general import profile or native AOT cache.
/// Returned arrays are defensive copies. Failure preserves the accepted entry.
/// </summary>
public sealed class FrozenCilSession
{
    private readonly object gate=new();
    private sealed record Pin(string Name,string Version,string Digest);
    private sealed record Entry(string Consumer,Pin[] Pins,byte[] Cil);
    private Entry? previous;
    public sealed record Build(byte[] Cil,bool Reused);
    public Build Compile(string consumer,IReadOnlyList<FrozenImportSnapshot> packages,bool cold=false)
    {
        lock(gate) {
            if(consumer is null||consumer.Length>65536||packages is null||packages.Count is <1 or >16)
                throw new FormatException("E_SOURCE_LIMIT: frozen scalar guest budget exceeded");
            var current=packages.ToArray();
            FrozenDependencyGraph.Verify(current);
            var pins=current.OrderBy(x=>x.Name,StringComparer.Ordinal)
                .Select(x=>new Pin(x.Name,x.Version,x.LockDigest)).ToArray();
            if(!cold&&previous is {} entry&&entry.Consumer==consumer&&entry.Pins.SequenceEqual(pins))
                return new((byte[])entry.Cil.Clone(),true);
            // Fresh ordinary compiler, full CFG and linkage verification, no
            // implicit fallback or acceptance of caller-supplied artifact.
            var module=OresStructuredParser.CompileFrozenScalar(consumer,current);
            var bytes=CilEmitter.BuildLibrary(module);
            if(bytes.Length>4*1024*1024)
                throw new FormatException("E_CACHE_OUTPUT: CIL artifact exceeds budget");
            previous=new(consumer,pins,bytes);
            return new((byte[])bytes.Clone(),false);
        }
    }
}
