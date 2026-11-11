using Ores.Net.Runtime;
using System.Text;
using System.Runtime.InteropServices;

// Concrete host SQLite integration; no emitted CIL or .atto guest involved.
internal static class DurableCommandInboxV1SelfTest
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static void Require(bool okay, string why)
    {
        if (!okay) throw new Exception("DURABLE_COMMAND_INBOX_V1_" + why);
    }
    private static void Denied(Action operation, string why)
    {
        try { operation(); } catch (InvalidOperationException) { return; }
        throw new Exception("DURABLE_COMMAND_INBOX_V1_NOT_DENIED_" + why);
    }
    // Separate SQLite connection for deterministic host fault injection.
    private static class SqliteFaults
    {
        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            out IntPtr db, int flags, IntPtr vfs);
        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr db,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            IntPtr callback, IntPtr arg, IntPtr error);
        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close_v2(IntPtr db);
    }
    private static void ExecRaw(string file,string statement)
    {
        if(SqliteFaults.sqlite3_open_v2(file,out var db,2|4|0x10000,IntPtr.Zero)!=0)
            throw new Exception("SQLite fault injector open failed");
        try
        {
            if(SqliteFaults.sqlite3_exec(db,statement,IntPtr.Zero,IntPtr.Zero,
                IntPtr.Zero)!=0)throw new Exception("SQLite fault injector execution failed");
        }
        finally { SqliteFaults.sqlite3_close_v2(db); }
    }
    public static void Run()
    {
        string dir=Path.Combine(Path.GetTempPath(),"atto-nativeaot-inbox-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"state.sqlite");
        try
        {
            using(var store=new DurableCommandInboxV1(path))
            {
                store.GrantEpoch("tenant-a","bank-42",1);
                var original=store.Commit("tenant-a","bank-42","main",1,"op-42",0,
                    B("Deposit(100)"),new[] {
                        new DurableCommandEventV1(1,B("MoneyDeposited(60)")),
                        new DurableCommandEventV1(1,B("MoneyDeposited(40)")) },B("Ok(balance=100)"));
                Require(!original.Duplicate&&original.FirstVersion==1&&original.LastVersion==2,"COMMIT");
                var repeated=store.Commit("tenant-a","bank-42","main",1,"op-42",0,
                    B("Deposit(100)"),new[] {new DurableCommandEventV1(1,B("wrong new reducer"))},B("wrong"));
                Require(repeated.Duplicate&&repeated.LastVersion==2&&
                    repeated.Reply.AsSpan().SequenceEqual(original.Reply),"ORIGINAL_REPLY");
                Denied(()=>store.Resolve("tenant-a","bank-42","main",1,"op-42",B("Deposit(200)")),"COMMAND_CONFLICT");
                Require(store.Resolve("tenant-a","bank-42","main",1,"unknown",B("Deposit(100)")) is null,"MISSING");
                store.GrantEpoch("tenant-a","bank-42",2);
                Denied(()=>store.Resolve("tenant-a","bank-42","main",1,"op-42",B("Deposit(100)")),"STALE_EPOCH");
                Denied(()=>store.Commit("tenant-a","bank-42","main",1,"old-op",2,B("Deposit(5)"),
                    new[] {new DurableCommandEventV1(1,B("5"))},B("ok")),"STALE_WRITE");
                var prior=store.Resolve("tenant-a","bank-42","main",2,"op-42",B("Deposit(100)"));
                Require(prior is not null&&prior.Duplicate&&prior.LastVersion==2&&
                    prior.Reply.AsSpan().SequenceEqual(B("Ok(balance=100)")),"RESTART_REPLY");
                Denied(()=>store.Commit("tenant-a","bank-42","main",2,"op-42",2,B("Deposit(200)"),
                    new[] {new DurableCommandEventV1(1,B("200"))},B("wrong")),"REUSED_OP_ID");
                Require(store.Commit("tenant-a","bank-42","main",2,"op-43",2,B("Deposit(5)"),
                    new[] {new DurableCommandEventV1(1,B("5"))},B("Ok(balance=105)")).LastVersion==3,"NEXT_VERSION");
            }
            using(var reopened=new DurableCommandInboxV1(path))
            {
                var stored=reopened.Resolve("tenant-a","bank-42","main",2,"op-42",B("Deposit(100)"));
                Require(stored is not null&&stored.Reply.AsSpan().SequenceEqual(B("Ok(balance=100)")),"REOPENED");
                var mutable=stored.Reply; mutable[0]=0;
                Require(reopened.Resolve("tenant-a","bank-42","main",2,"op-42",B("Deposit(100)"))!
                    .Reply.AsSpan().SequenceEqual(B("Ok(balance=100)")),"OWNED_REPLY");
                reopened.GrantEpoch("tenant-a","bank-43",1);
                Require(reopened.Resolve("tenant-a","bank-43","main",1,"op-42",B("Deposit(100)")) is null,
                    "WRONG_ACTOR");
            }
            using(var store=new DurableCommandInboxV1(path))
            {
                store.GrantEpoch("tenant-a","bank-fault",1);
                ExecRaw(path,"CREATE TRIGGER abort_second BEFORE INSERT ON durable_events " +
                    "WHEN NEW.actor='bank-fault' AND NEW.version=2 " +
                    "BEGIN SELECT RAISE(ABORT,'injected'); END");
                Denied(()=>store.Commit("tenant-a","bank-fault","main",1,"op-fault",0,
                    B("Deposit(10)"),new[] {
                        new DurableCommandEventV1(1,B("6")),
                        new DurableCommandEventV1(1,B("4")) }, B("Ok(10)")),"SECOND_EVENT_ROLLBACK");
                Require(store.Resolve("tenant-a","bank-fault","main",1,"op-fault",
                    B("Deposit(10)")) is null,"ABORTED_COMMAND_PERSISTED");
                ExecRaw(path,"DROP TRIGGER abort_second");
                Require(store.Commit("tenant-a","bank-fault","main",1,"op-fault",0,
                    B("Deposit(10)"),new[] {
                        new DurableCommandEventV1(1,B("6")),
                        new DurableCommandEventV1(1,B("4")) }, B("Ok(10)")).LastVersion==2,
                    "RETRY_AFTER_CONFIRMED_ROLLBACK");
                try
                {
                    store.Commit("tenant-a","bank-fault","main",1,"oversized",2,
                        B("cmd"),new[] {new DurableCommandEventV1(1,new byte[65537])},B("ok"));
                    throw new Exception("oversized event was accepted");
                }
                catch(ArgumentException) { /* bound enforcement proven */ }
            }
            // Durable inbox reply must be rejected if its journal is corrupted.
            ExecRaw(path,"DELETE FROM durable_events WHERE actor='bank-42' AND version=1");
            using(var store=new DurableCommandInboxV1(path))
                Denied(()=>store.Resolve("tenant-a","bank-42","main",2,
                    "op-42",B("Deposit(100)")),"CORRUPTED_JOURNAL");
            Console.WriteLine("NET_DURABLE_HOST_PASS|atomic_events=2|cross_epoch_reply=1|nativeaot_test_host_only=1|guest_atto=0");
        }
        finally { Directory.Delete(dir,true); }
    }
}
