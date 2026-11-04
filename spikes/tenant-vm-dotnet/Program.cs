// A .NET process hosting 1000 tenant VMs in-process, with the CLR doing what it normally does
// alongside: garbage collecting, and using SIGSEGV for managed null checks.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

static unsafe class Native {
    const string L = "tenantvm";
    [DllImport(L)] public static extern IntPtr ores_vm_c_host_new(uint carriers, uint maxVms);
    [DllImport(L)] public static extern void ores_vm_c_host_free(IntPtr h);
    [DllImport(L)] public static extern IntPtr ores_vm_c_spawn(IntPtr h, IntPtr vm, uint kind, out int status);
    [DllImport(L)] public static extern int ores_vm_c_call(IntPtr h, IntPtr actor, byte* input, uint inputLength, byte* output, uint outputCapacity, out uint outputLength);
    [DllImport(L)] public static extern int ores_vm_c_kill(IntPtr h, IntPtr vm);
    [DllImport(L)] public static extern int ores_vm_c_wait_closed(IntPtr h, IntPtr vm);
    [DllImport(L)] public static extern IntPtr ores_vm_c_reason_name(int reason);
    [DllImport(L)] public static extern IntPtr ores_vm_c_guest_load([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string sha256);
    [DllImport(L)] public static extern IntPtr ores_vm_c_open_guest(IntPtr h, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, IntPtr guest, uint deadlineMs, out int status);
}

static unsafe class Program {
    const long OpAdd = 0, OpGrow = 1, OpPeek = 2, OpPoke = 4, OpTrap = 5, OpSpin = 6;

    static long Call(IntPtr host, IntPtr actor, long op, long a, long b, out int status) {
        long* input = stackalloc long[3];
        input[0] = op; input[1] = a; input[2] = b;
        long output = -999999;
        status = Native.ores_vm_c_call(host, actor, (byte*)input, 24, (byte*)&output, 8, out uint _);
        return output;
    }
    static string Reason(int r) => Marshal.PtrToStringUTF8(Native.ores_vm_c_reason_name(r));

    static string s_null;

    // Arguments: a lowered guest library and its digest, from tools/tenant-vm/lower_guest.py.
    static int Main(string[] args) {
        IntPtr module = Native.ores_vm_c_guest_load(args[0], args[1]);
        if (module == IntPtr.Zero) throw new Exception("guest library refused");
        if (Native.ores_vm_c_guest_load(args[0], new string('0', 64)) != IntPtr.Zero) throw new Exception("wrong digest accepted");
        IntPtr host = Native.ores_vm_c_host_new(8, 1000);
        var aot = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
        Console.WriteLine($"runtime: {(aot ? "NativeAOT" : "CoreCLR JIT")}, GC server={System.Runtime.GCSettings.IsServerGC}");

        // Keep the CLR busy: allocation pressure and forced GCs, plus real null-check faults.
        var stop = false;
        long collections = 0, nullsCaught = 0;
        var noise = new Thread(() => {
            var keep = new List<byte[]>();
            while (!Volatile.Read(ref stop)) {
                keep.Add(new byte[4096]);
                if (keep.Count > 2000) { keep.Clear(); GC.Collect(); collections++; }
                try { _ = s_null.Length; } catch (NullReferenceException) { nullsCaught++; }
            }
        });
        noise.Start();

        const int Vms = 1000, Actors = 10;
        var sw = Stopwatch.StartNew();
        var vms = new IntPtr[Vms];
        var actors = new IntPtr[Vms, Actors];
        for (int v = 0; v < Vms; v++) {
            vms[v] = Native.ores_vm_c_open_guest(host, "tenant-" + v, module, 2000, out int st);
            if (st != 0) throw new Exception("open " + st);
            for (int a = 0; a < Actors; a++) {
                actors[v, a] = Native.ores_vm_c_spawn(host, vms[v], 0, out st);
                if (st != 0) throw new Exception("spawn " + st);
            }
        }
        Console.WriteLine($"{Vms} VMs, {Vms * Actors} actors in pid {Environment.ProcessId}: {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        int turns = 0;
        for (int round = 1; round <= 3; round++)
            for (int v = 0; v < Vms; v++)
                for (int a = 0; a < Actors; a++) {
                    long total = Call(host, actors[v, a], OpAdd, v * 100 + a, 0, out int st);
                    if (st != 0 || total != (v * 100L + a) * round) throw new Exception($"tenant {v} wrong: st={st} {total}");
                    turns++;
                }
        Console.WriteLine($"{turns} turns, correct per tenant: {sw.ElapsedMilliseconds}ms");

        // Faults in guest code, while the CLR faults for its own reasons on other threads.
        var survivor = actors[0, 0];
        long expected = Call(host, survivor, OpAdd, 0, 0, out _);
        foreach (var (name, op, deadline) in new[] { ("trap", OpTrap, 2000u), ("oob-read", OpPeek, 2000u), ("spin", OpSpin, 100u) }) {
            for (int i = 0; i < 20; i++) {
                IntPtr v = Native.ores_vm_c_open_guest(host, "x", module, deadline, out int st);
                // The VM limit is 1000 and all are in use, so retire one to make room below.
                if (st != 0) { var old = vms[999 - i % 500]; Native.ores_vm_c_kill(host, old); Native.ores_vm_c_wait_closed(host, old); i--; continue; }
                IntPtr a = Native.ores_vm_c_spawn(host, v, 0, out st);
                Call(host, a, op, op == OpPeek ? 0x70000000L : 0, 0, out st);
                int reason = Native.ores_vm_c_wait_closed(host, v);
                if (i == 0) Console.WriteLine($"  {name,-9} -> call status {st}, VM ended: {Reason(reason)}");
                expected += 1;
                if (Call(host, survivor, OpAdd, 1, 0, out st) != expected) throw new Exception("survivor damaged");
            }
        }
        Volatile.Write(ref stop, true);
        noise.Join();
        Console.WriteLine($"during this the CLR ran {collections} forced GCs, caught {nullsCaught} managed null faults; gen0={GC.CollectionCount(0)}");
        if (nullsCaught == 0) throw new Exception("null check path not exercised");
        Native.ores_vm_c_host_free(host);
        Console.WriteLine("DOTNET_SPIKE_PASS");
        return 0;
    }
}
