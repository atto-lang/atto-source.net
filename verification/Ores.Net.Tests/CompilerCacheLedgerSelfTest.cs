using System;
using System.Linq;
using System.Security.Cryptography;
using Ores.Net.Compiler;
internal static class CompilerCacheLedgerSelfTest {
    internal static void Run(){
        var master=Enumerable.Repeat((byte)7,32).ToArray();
        var record=CompilerCacheLedger.Record("tenant/a",2,master);var pin=SHA256.HashData(record);
        static void Same(string hex,byte[] actual){if(!Convert.FromHexString(hex).AsSpan().SequenceEqual(actual))throw new InvalidOperationException("ledger vector mismatch");}
        static void Deny(Action action){try{action();}catch(FormatException e)when(e.Message.StartsWith("E_CACHE_LEDGER:",StringComparison.Ordinal)){return;}throw new InvalidOperationException("invalid ledger admitted");}
        Same("4154544f4c4544310000000874656e616e742f610000000000000002a63c8f18ca3245c9d319f61b2ca45a2a6605ea3293664ad236792133ce088462",record);Same("95c69714073e8a9a4c845fa54d0f87706f718fed1f5879016723780618b83309",pin);
        Same("d2153371616fc59b235d24aff7417a37fea38a6fdc5391503a54ea33f429a90e",CompilerCacheLedger.Admit("tenant/a",record,pin,master));
        Deny(()=>CompilerCacheLedger.Admit("tenant/a",CompilerCacheLedger.Record("tenant/a",1,master),pin,master));
        Deny(()=>CompilerCacheLedger.Admit("tenant/b",record,pin,master));
        Deny(()=>CompilerCacheLedger.Admit("tenant/a",record,pin,new byte[32]));
        var wrong=(byte[])master.Clone();wrong[0]^=1;Deny(()=>CompilerCacheLedger.Admit("tenant/a",record,pin,wrong));
        foreach(int i in new[]{0,8,12,20,record.Length-1}){var bad=(byte[])record.Clone();bad[i]^=1;Deny(()=>CompilerCacheLedger.Admit("tenant/a",bad,pin,master));}
        foreach(int n in new[]{0,1,record.Length-1,record.Length+1,565}){var bad=new byte[n];Array.Copy(record,bad,Math.Min(n,record.Length));Deny(()=>CompilerCacheLedger.Admit("tenant/a",bad,pin,master));}
        var high=CompilerCacheLedger.Record("tenant/a",ulong.MaxValue,master);
        if(CompilerCacheLedger.Admit("tenant/a",high,SHA256.HashData(high),master).Length!=32)throw new InvalidOperationException("high epoch");
        Deny(()=>CompilerCacheLedger.Record("tenant/a",0,master));
        Console.WriteLine("CACHE_LEDGER_NET|pin=95c69714073e8a9a4c845fa54d0f87706f718fed1f5879016723780618b83309|rollback-rejected|bounded");
    }
}
