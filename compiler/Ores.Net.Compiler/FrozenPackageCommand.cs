using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ores.Net.Compiler;

// Explicit opt-in command on the real CLI; no default discovery or
// capability flags. This emits bounded verified CIL, not a native executable.
internal static class FrozenPackageCommand
{
    internal static int Run(string[] args)
    {
        bool compile=args[0]=="compile-frozen";
        int end=args.Length;
        string? output=null;
        if(compile) {
            if(end<4||args[^2]!="--out"||Path.GetFileName(args[^1])!="OresGenerated.dll")
                throw new ArgumentException("compile-frozen requires --out OresGenerated.dll");
            output=args[^1]; end-=2;
        }
        if(end<7||(end-2)%5!=0||(end-2)/5>16)
            throw new ArgumentException("usage: check-frozen|compile-frozen INPUT.atto --package ZIP NAME VERSION TRUSTED_LOCK_SHA256 [--package ...] [--out OresGenerated.dll]");
        if(!args[1].EndsWith(".atto",StringComparison.Ordinal))
            throw new FormatException("E_IMPORT_PATH: frozen consumer must be .atto");
        var packages=new List<FrozenImportSnapshot>();
        for(int i=2;i<end;i+=5) {
            if(args[i]!="--package") throw new ArgumentException("E_IMPORT_PATH: expected --package");
            packages.Add(FrozenPackageArchive.Read(args[i+1],args[i+2],args[i+3],args[i+4]));
        }
        string consumer=new UTF8Encoding(false,true).GetString(FrozenPackageArchive.ReadBounded(args[1],65536));
        var verified=OresStructuredParser.CompileFrozenScalar(consumer,packages);
        if(output is not null) {
            // Stage output in the same directory, only publish on success.
            string full=Path.GetFullPath(output);
            string parent=Path.GetDirectoryName(full)!;
            string staged=Path.Combine(parent,".frozen-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staged);
            string artifact=Path.Combine(staged,"OresGenerated.dll");
            try {
                CilEmitter.EmitLibrary(verified,artifact);
                File.Move(artifact,full,true);
            } finally { Directory.Delete(staged,true); }
        }
        Console.WriteLine("FROZEN_CHECK|scope=bounded-scalar|packages="+packages.Count+"|cil="+(compile?"emitted":"not-emitted"));
        return 0;
    }
}
