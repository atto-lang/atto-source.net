using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ores.Net.Compiler;

// Linux x64 statx ABI, not libc's architecture-dependent struct stat.
// Host must protect ancestors, mounts, procfs and exclude noncooperating writers.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class LinuxCacheDirectory:IDisposable
{
    private const int ReadOnly=0,WriteOnly=1,Create=0x40,Exclusive=0x80,Nonblock=0x800,
        DirectoryFlag=0x10000,NoFollow=0x20000,CloseExec=0x80000,PathFlag=0x200000,
        AtFdcwd=-100,AtEmptyPath=0x1000,LockExclusive=2,LockNonblock=4;
    private const int NoEntry=2;
    [StructLayout(LayoutKind.Explicit,Size=256)]
    private struct Statx {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;
    }
    internal readonly record struct Identity(ulong Inode,uint Major,uint Minor);
    [DllImport("libc",SetLastError=true,EntryPoint="openat")]
    private static extern int OpenAt(int fd,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,int flags,uint mode);
    [DllImport("libc",SetLastError=true,EntryPoint="statx")]
    private static extern int Stat(int fd,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,int flags,uint mask,out Statx stat);
    [DllImport("libc",SetLastError=true,EntryPoint="geteuid")]
    private static extern uint Euid();
    [DllImport("libc",SetLastError=true,EntryPoint="flock")]
    private static extern int Flock(int fd,int operation);
    [DllImport("libc",SetLastError=true,EntryPoint="unlinkat")]
    private static extern int Unlink(int fd,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,int flags);
    [DllImport("libc",SetLastError=true,EntryPoint="renameat")]
    private static extern int Rename(int from,[MarshalAs(UnmanagedType.LPUTF8Str)]string old,int to,[MarshalAs(UnmanagedType.LPUTF8Str)]string next);
    [DllImport("libc",SetLastError=true,EntryPoint="fsync")]
    private static extern int Sync(int fd);
    private readonly SafeFileHandle root;
    private static int Fd(SafeFileHandle h)=>h.DangerousGetHandle().ToInt32();
    private static IOException Error(string operation)=>new("E_CACHE_PATH: "+operation+" errno="+Marshal.GetLastPInvokeError());
    private static SafeFileHandle Handle(int fd)=>fd<0?throw Error("open"):new SafeFileHandle(new IntPtr(fd),true);
    private static Statx Info(SafeFileHandle h){
        if(Stat(Fd(h),"",AtEmptyPath,0x7ff,out var stat)!=0)throw Error("statx");
        if((stat.Mask&0x30f)!=0x30f)throw new IOException("E_CACHE_PATH: incomplete inode metadata");
        return stat;
    }
    private static Identity Id(Statx s)=>new(s.Inode,s.DevMajor,s.DevMinor);
    private static void Private(Statx s,int type){
        if(s.Uid!=Euid()||(s.Mode&0x3f)!=0||(s.Mode&0xf000)!=type)throw new IOException("E_CACHE_PATH: private inode required");
    }
    private static void Regular(Statx s,int bound){
        Private(s,0x8000);
        if(s.Links!=1||s.Size<(ulong)CompilerArtifactReceipt.Header||s.Size>(ulong)bound)throw new IOException("E_CACHE_PATH: bounded regular inode required");
    }
    private LinuxCacheDirectory(SafeFileHandle root){this.root=root;RootIdentity=Id(Info(root));}
    internal Identity RootIdentity{get;}
    internal static LinuxCacheDirectory Open(string path){
        var fd=Handle(OpenAt(AtFdcwd,"/",ReadOnly|DirectoryFlag|CloseExec,0));
        try{
            foreach(var name in path.Split('/',StringSplitOptions.RemoveEmptyEntries)){
                if(name==".."||name==".")throw new IOException("E_CACHE_PATH: traversal");
                var next=Handle(OpenAt(Fd(fd),name,ReadOnly|DirectoryFlag|NoFollow|CloseExec,0));fd.Dispose();fd=next;
            }
            Private(Info(fd),0x4000);return new LinuxCacheDirectory(fd);
        }catch{fd.Dispose();throw;}
    }
    internal void Lock(){if(Flock(Fd(root),LockExclusive|LockNonblock)!=0)throw Error("writer busy");}
    internal byte[]? Read(string name,int bound){
        int raw=OpenAt(Fd(root),name,PathFlag|NoFollow|CloseExec,0);
        if(raw<0&&Marshal.GetLastPInvokeError()==NoEntry)return null;
        using var pinned=Handle(raw);var old=Info(pinned);Regular(old,bound);
        using var file=Handle(OpenAt(AtFdcwd,"/proc/self/fd/"+Fd(pinned),ReadOnly|Nonblock|CloseExec,0));
        var now=Info(file);Regular(now,bound);
        if(Id(now)!=Id(old))throw new IOException("E_CACHE_PATH: inode changed");
        using var input=new FileStream(file,FileAccess.Read);
        byte[] result=new byte[(int)now.Size];input.ReadExactly(result);
        if(input.ReadByte()!=-1)throw new IOException("E_CACHE_PATH: artifact grew");return result;
    }
    private void RemovePending(){if(Unlink(Fd(root),".pending",0)!=0&&Marshal.GetLastPInvokeError()!=NoEntry)throw Error("unlink pending");}
    internal void Publish(string name,byte[] bytes,int bound){
        if(bytes.Length>bound)throw new IOException("E_CACHE_PATH: artifact size");
        RemovePending();bool owned=false;
        try{
            using(var file=Handle(OpenAt(Fd(root),".pending",WriteOnly|Create|Exclusive|NoFollow|CloseExec,0x180))){
                owned=true;using var output=new FileStream(file,FileAccess.Write);output.Write(bytes);output.Flush(true);
            }
            if(Rename(Fd(root),".pending",Fd(root),name)!=0)throw Error("rename");owned=false;
            // Failure here is indeterminate durability after a visible rename.
            if(Sync(Fd(root))!=0)throw Error("directory fsync");
        }finally{if(owned)RemovePending();}
    }
    public void Dispose()=>root.Dispose();
}
