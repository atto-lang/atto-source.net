using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Ores.Net.Runtime;

/// <summary>Owned immutable event bytes; the host owns these, not the Atto guest.</summary>
public sealed class DurableCommandEventV1(int schemaVersion, byte[] payload)
{
    private readonly byte[] _payload = (byte[])(payload ?? throw new ArgumentNullException(nameof(payload))).Clone();
    public int SchemaVersion { get; } = schemaVersion > 0 ? schemaVersion : throw new ArgumentOutOfRangeException(nameof(schemaVersion));
    public byte[] Payload => (byte[])_payload.Clone();
    internal byte[] OwnedPayload => _payload;
}

public sealed class DurableCommandReceiptV1(long firstVersion, long lastVersion, byte[] reply, bool duplicate)
{
    private readonly byte[] _reply = (byte[])(reply ?? throw new ArgumentNullException(nameof(reply))).Clone();
    public long FirstVersion { get; } = firstVersion;
    public long LastVersion { get; } = lastVersion;
    public bool Duplicate { get; } = duplicate;
    public byte[] Reply => (byte[])_reply.Clone();
}

/// <summary>
/// Trusted synchronous SQLite WAL/FULL command provider for Native AOT hosts.
/// Uses static SQLite C calls, no managed SQLite ORM, reflection or JIT.
/// Caller MUST invoke it on a bounded storage worker, not an actor carrier.
/// No guest may select DB paths, tenant identity, epochs, or stored replies.
/// sqlite3 native library availability is a deployment prerequisite.
/// </summary>
public sealed class DurableCommandInboxV1 : IDisposable
{
    private const int Row = 100, Done = 101, BlobType = 4, NullType = 5;
    private const int OpenReadWrite = 0x2, OpenCreate = 0x4, OpenFullMutex = 0x10000;
    private static readonly IntPtr Transient = new(-1);
    private readonly object _gate = new();
    private IntPtr _db;

    // Source-generated native interop: every argument is blittable. SQL and
    // filenames are explicitly UTF-8, pinned only for the duration of a call.
    // SQLite copies bound text/blob bytes because SQLITE_TRANSIENT is used.
    private static partial class Native
    {
        [LibraryImport("sqlite3", EntryPoint="sqlite3_open_v2")]
        internal static unsafe partial int sqlite3_open_v2(byte* path,
            out IntPtr db, int flags, IntPtr vfs);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_close_v2")]
        internal static partial int sqlite3_close_v2(IntPtr db);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_exec")]
        internal static unsafe partial int sqlite3_exec(IntPtr db,byte* sql,
            IntPtr callback,IntPtr argument,IntPtr errorMessage);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_prepare_v2")]
        internal static unsafe partial int sqlite3_prepare_v2(IntPtr db,byte* sql,
            int bytes,out IntPtr statement,IntPtr tail);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_finalize")]
        internal static partial int sqlite3_finalize(IntPtr statement);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_step")]
        internal static partial int sqlite3_step(IntPtr statement);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_reset")]
        internal static partial int sqlite3_reset(IntPtr statement);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_clear_bindings")]
        internal static partial int sqlite3_clear_bindings(IntPtr statement);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_bind_text")]
        internal static unsafe partial int sqlite3_bind_text(IntPtr statement,int index,
            byte* text,int bytes,IntPtr destructor);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_bind_int64")]
        internal static partial int sqlite3_bind_int64(IntPtr statement,int index,long value);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_bind_blob")]
        internal static unsafe partial int sqlite3_bind_blob(IntPtr statement,int index,
            byte* bytes,int size,IntPtr destructor);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_bind_zeroblob")]
        internal static partial int sqlite3_bind_zeroblob(IntPtr statement,int index,int size);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_column_int64")]
        internal static partial long sqlite3_column_int64(IntPtr statement,int column);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_column_bytes")]
        internal static partial int sqlite3_column_bytes(IntPtr statement,int column);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_column_type")]
        internal static partial int sqlite3_column_type(IntPtr statement,int column);
        [LibraryImport("sqlite3", EntryPoint="sqlite3_column_blob")]
        internal static partial IntPtr sqlite3_column_blob(IntPtr statement,int column);
    }

    private sealed class Query(DurableCommandInboxV1 store, string sql) : IDisposable
    {
        private IntPtr _stmt = Prepare(store._db, sql);
        private static IntPtr Prepare(IntPtr db,string sql)
        {
            byte[] sqlUtf8=Encoding.UTF8.GetBytes(sql+"\0");
            unsafe
            {
                fixed(byte* ptr=sqlUtf8)
                {
                    Check(Native.sqlite3_prepare_v2(db,ptr,-1,out var stmt,IntPtr.Zero));
                    return stmt;
                }
            }
        }
        public IntPtr Handle => _stmt != IntPtr.Zero ? _stmt : throw new ObjectDisposedException(nameof(Query));
        public Query Text(int index,string text)
        {
            byte[] utf8=Encoding.UTF8.GetBytes(text);
            unsafe
            {
                fixed(byte* ptr=utf8)
                    Check(Native.sqlite3_bind_text(Handle,index,ptr,utf8.Length,Transient));
            }
            return this;
        }
        public Query Integer(int index,long value)
        {
            Check(Native.sqlite3_bind_int64(Handle,index,value));return this;
        }
        public Query Blob(int index,byte[] value)
        {
            if(value.Length==0)Check(Native.sqlite3_bind_zeroblob(Handle,index,0));
            else unsafe
            {
                fixed(byte* ptr=value)
                    Check(Native.sqlite3_bind_blob(Handle,index,ptr,value.Length,Transient));
            }
            return this;
        }
        public int Step()
        {
            int rc=Native.sqlite3_step(Handle);
            if(rc!=Row && rc!=Done)throw new InvalidOperationException("SQLITE_COMMAND_OUTCOME_UNKNOWN");
            return rc;
        }
        public long Long(int index) => Native.sqlite3_column_int64(Handle,index);
        public byte[] Bytes(int index,int limit)
        {
            if(Native.sqlite3_column_type(Handle,index)==NullType)throw new InvalidOperationException("CORRUPT_COMMAND_RECORD");
            int count=Native.sqlite3_column_bytes(Handle,index);
            if(count<0||count>limit)throw new InvalidOperationException("CORRUPT_COMMAND_RECORD");
            byte[] result=new byte[count];
            if(count>0)
            {
                IntPtr source=Native.sqlite3_column_blob(Handle,index);
                if(source==IntPtr.Zero)throw new InvalidOperationException("CORRUPT_COMMAND_RECORD");
                Marshal.Copy(source,result,0,count);
            }
            return result;
        }
        public void Reset()
        {
            Native.sqlite3_reset(Handle);
            Check(Native.sqlite3_clear_bindings(Handle));
        }
        public void Dispose()
        {
            if(_stmt!=IntPtr.Zero){Native.sqlite3_finalize(_stmt);_stmt=IntPtr.Zero;}
        }
    }

    private static void Check(int code)
    {
        if(code!=0)throw new InvalidOperationException("SQLITE_COMMAND_OUTCOME_UNKNOWN");
    }
    private static bool IdOk(string value)
    {
        if(string.IsNullOrEmpty(value)||value.Length>128)return false;
        foreach(char c in value)
            if(!((c is >= 'a' and <= 'z')||(c is >= 'A' and <= 'Z')||
                 (c is >= '0' and <= '9')||c is '-' or '_' or '.'))return false;
        return true;
    }
    private static void Verify(string tenant,string actor,string slot,string op)
    {
        if(!IdOk(tenant)||!IdOk(actor)||!IdOk(slot)||!IdOk(op))
            throw new ArgumentException("INVALID_COMMAND_IDENTITY");
    }
    private void Sql(string sql)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(sql+"\0");
        unsafe
        {
            fixed(byte* ptr=bytes)
                Check(Native.sqlite3_exec(_db,ptr,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero));
        }
    }
    private void Rollback(){try{Sql("ROLLBACK");}catch(InvalidOperationException){ /* ambiguous outcome */ }}
    private static Query Key(Query q,string tenant,string actor,string slot) =>
        q.Text(1,tenant).Text(2,actor).Text(3,slot);
    private long Epoch(string tenant,string actor)
    {
        using var q=new Query(this,"SELECT epoch FROM durable_fences WHERE tenant=? AND actor=?");
        q.Text(1,tenant).Text(2,actor);
        if(q.Step()==Done)return 0;
        long e=q.Long(0);if(e<=0)throw new InvalidOperationException("CORRUPT_FENCE");return e;
    }
    private long Version(string tenant,string actor,string slot)
    {
        using var q=Key(new Query(this,"SELECT version FROM durable_head WHERE tenant=? AND actor=? AND slot=?"),tenant,actor,slot);
        if(q.Step()==Done)return 0;
        long n=q.Long(0);if(n<0)throw new InvalidOperationException("CORRUPT_HEAD");return n;
    }
    public DurableCommandInboxV1(string trustedDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedDatabasePath);
        if(!Path.IsPathFullyQualified(trustedDatabasePath)||trustedDatabasePath.StartsWith("file:",StringComparison.OrdinalIgnoreCase)||
            trustedDatabasePath.Contains('\0'))throw new ArgumentException("TRUSTED_DATABASE_PATH_REQUIRED");
        byte[] pathBytes=Encoding.UTF8.GetBytes(trustedDatabasePath+"\0");
        unsafe
        {
            fixed(byte* ptr=pathBytes)
                Check(Native.sqlite3_open_v2(ptr,out _db,
                    OpenReadWrite|OpenCreate|OpenFullMutex,IntPtr.Zero));
        }
        try
        {
            Sql("PRAGMA busy_timeout=5000");Sql("PRAGMA journal_mode=WAL");Sql("PRAGMA synchronous=FULL");
            using(var mode=new Query(this,"PRAGMA journal_mode"))
            {
                if(mode.Step()!=Row || !Encoding.UTF8.GetString(mode.Bytes(0,16)).Equals("wal",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SQLITE_WAL_UNAVAILABLE");
            }
            Sql("CREATE TABLE IF NOT EXISTS durable_fences(tenant TEXT NOT NULL,actor TEXT NOT NULL,epoch INTEGER NOT NULL,PRIMARY KEY(tenant,actor))");
            Sql("CREATE TABLE IF NOT EXISTS durable_head(tenant TEXT NOT NULL,actor TEXT NOT NULL,slot TEXT NOT NULL,version INTEGER NOT NULL,PRIMARY KEY(tenant,actor,slot))");
            Sql("CREATE TABLE IF NOT EXISTS durable_events(tenant TEXT NOT NULL,actor TEXT NOT NULL,slot TEXT NOT NULL,version INTEGER NOT NULL,schema INTEGER NOT NULL,payload BLOB NOT NULL,PRIMARY KEY(tenant,actor,slot,version))");
            Sql("CREATE TABLE IF NOT EXISTS durable_command_inbox(tenant TEXT NOT NULL,actor TEXT NOT NULL,slot TEXT NOT NULL,op TEXT NOT NULL,epoch INTEGER NOT NULL,expected INTEGER NOT NULL,request BLOB NOT NULL,first_version INTEGER NOT NULL,last_version INTEGER NOT NULL,event_batch BLOB NOT NULL,reply BLOB NOT NULL,PRIMARY KEY(tenant,actor,slot,op))");
        }
        catch {Dispose();throw;}
    }
    private static byte[] Encode(IReadOnlyList<DurableCommandEventV1> events)
    {
        if(events.Count is < 1 or > 256)throw new ArgumentOutOfRangeException(nameof(events));
        int size=4;
        foreach(var e in events)
        {
            if(e.SchemaVersion<=0||e.OwnedPayload.Length>65536||size>1048576-8-e.OwnedPayload.Length)
                throw new ArgumentException("INVALID_EVENT_BATCH");
            size+=8+e.OwnedPayload.Length;
        }
        byte[] data=new byte[size];BinaryPrimitives.WriteInt32BigEndian(data,events.Count);
        int offset=4;
        foreach(var e in events)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset),e.SchemaVersion);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset+4),e.OwnedPayload.Length);
            e.OwnedPayload.AsSpan().CopyTo(data.AsSpan(offset+8));offset+=8+e.OwnedPayload.Length;
        }
        return data;
    }
    private static List<DurableCommandEventV1> Decode(byte[] bytes)
    {
        if(bytes.Length is < 12 or > 1048576)throw new InvalidOperationException("CORRUPT_COMMAND_BATCH");
        int count=BinaryPrimitives.ReadInt32BigEndian(bytes);
        if(count is < 1 or > 256)throw new InvalidOperationException("CORRUPT_COMMAND_BATCH");
        int offset=4;var events=new List<DurableCommandEventV1>(count);
        for(int i=0;i<count;i++)
        {
            if(offset>bytes.Length-8)throw new InvalidOperationException("CORRUPT_COMMAND_BATCH");
            int schema=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
            int n=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset+4));offset+=8;
            if(schema<=0||n<0||n>65536||n>bytes.Length-offset)
                throw new InvalidOperationException("CORRUPT_COMMAND_BATCH");
            events.Add(new DurableCommandEventV1(schema,bytes.AsSpan(offset,n).ToArray()));offset+=n;
        }
        if(offset!=bytes.Length)throw new InvalidOperationException("CORRUPT_COMMAND_BATCH");
        return events;
    }
    private DurableCommandReceiptV1? Lookup(string tenant,string actor,string slot,long epoch,string op,byte[] request)
    {
        using var q=Key(new Query(this,"SELECT epoch,expected,request,first_version,last_version,event_batch,reply FROM durable_command_inbox WHERE tenant=? AND actor=? AND slot=? AND op=?"),tenant,actor,slot);
        q.Text(4,op);
        if(q.Step()==Done)return null;
        long sourceEpoch=q.Long(0), expected=q.Long(1), first=q.Long(3),last=q.Long(4);
        if(sourceEpoch<=0||sourceEpoch>epoch||expected<0||expected==long.MaxValue||
            first!=expected+1||last<first||last-first>=256||last>Version(tenant,actor,slot))
            throw new InvalidOperationException("OP_ID_CONFLICT");
        if(!q.Bytes(2,65536).AsSpan().SequenceEqual(request))
            throw new InvalidOperationException("OP_ID_CONFLICT");
        var events=Decode(q.Bytes(5,1048576));
        byte[] reply=q.Bytes(6,65536);
        if(events.Count!=last-first+1)throw new InvalidOperationException("CORRUPT_COMMAND_BATCH");
        using var journal=Key(new Query(this,"SELECT version,schema,payload FROM durable_events WHERE tenant=? AND actor=? AND slot=? AND version>=? AND version<=? ORDER BY version ASC"),tenant,actor,slot);
        journal.Integer(4,first).Integer(5,last);
        for(int i=0;i<events.Count;i++)
        {
            if(journal.Step()!=Row||journal.Long(0)!=first+i||journal.Long(1)!=events[i].SchemaVersion||
               !journal.Bytes(2,65536).AsSpan().SequenceEqual(events[i].OwnedPayload))
                throw new InvalidOperationException("INCOMPLETE_COMMAND_JOURNAL");
        }
        if(journal.Step()!=Done)throw new InvalidOperationException("INCOMPLETE_COMMAND_JOURNAL");
        return new DurableCommandReceiptV1(first,last,reply,true);
    }
    public void GrantEpoch(string tenant,string actor,long epoch)
    {
        Verify(tenant,actor,"main","valid");if(epoch<=0)throw new ArgumentOutOfRangeException(nameof(epoch));
        lock(_gate)
        {
            Sql("BEGIN IMMEDIATE");try
            {
                if(epoch<Epoch(tenant,actor))throw new InvalidOperationException("STALE_EPOCH");
                using(var q=new Query(this,"INSERT INTO durable_fences VALUES(?,?,?) ON CONFLICT(tenant,actor) DO UPDATE SET epoch=excluded.epoch"))
                    q.Text(1,tenant).Text(2,actor).Integer(3,epoch).Step();
                Sql("COMMIT");
            }catch{Rollback();throw;}
        }
    }
    public DurableCommandReceiptV1? Resolve(string tenant,string actor,string slot,long epoch,string op,byte[] request)
    {
        Verify(tenant,actor,slot,op);
        if(epoch<=0||request is null||request.Length>65536)throw new ArgumentException("INVALID_COMMAND_LOOKUP");
        byte[] copy=(byte[])request.Clone();
        lock(_gate)
        {
            Sql("BEGIN");try
            {
                if(Epoch(tenant,actor)!=epoch)throw new InvalidOperationException("STALE_EPOCH");
                var result=Lookup(tenant,actor,slot,epoch,op,copy);
                Sql("COMMIT");return result;
            }catch{Rollback();throw;}
        }
    }
    public DurableCommandReceiptV1 Commit(string tenant,string actor,string slot,long epoch,string op,
        long expected,byte[] request,IReadOnlyList<DurableCommandEventV1> events,byte[] reply)
    {
        Verify(tenant,actor,slot,op);
        if(epoch<=0||expected<0||request is null||reply is null||events is null||
            request.Length>65536||reply.Length>65536||events.Count>long.MaxValue-expected)
            throw new ArgumentException("INVALID_COMMAND_BOUNDS");
        byte[] ownedRequest=(byte[])request.Clone(), ownedReply=(byte[])reply.Clone();
        var ownedEvents=events.Select(e=>new DurableCommandEventV1(e.SchemaVersion,e.Payload)).ToArray();
        byte[] batch=Encode(ownedEvents);
        lock(_gate)
        {
            Sql("BEGIN IMMEDIATE");try
            {
                if(Epoch(tenant,actor)!=epoch)throw new InvalidOperationException("STALE_EPOCH");
                var prior=Lookup(tenant,actor,slot,epoch,op,ownedRequest);
                if(prior is not null){Sql("COMMIT");return prior;}
                long current=Version(tenant,actor,slot);
                if(current!=expected)throw new InvalidOperationException("VERSION_CONFLICT");
                long first=expected+1,last=expected+ownedEvents.Length;
                using(var q=Key(new Query(this,"INSERT INTO durable_head VALUES(?,?,?,?) ON CONFLICT(tenant,actor,slot) DO UPDATE SET version=excluded.version"),tenant,actor,slot))
                    q.Integer(4,last).Step();
                foreach(var (value,index) in ownedEvents.Select((x,i)=>(x,i)))
                {
                    using var q=Key(new Query(this,"INSERT INTO durable_events VALUES(?,?,?,?,?,?)"),tenant,actor,slot);
                    q.Integer(4,first+index).Integer(5,value.SchemaVersion).Blob(6,value.OwnedPayload).Step();
                }
                using(var q=Key(new Query(this,"INSERT INTO durable_command_inbox VALUES(?,?,?,?,?,?,?,?,?,?,?)"),tenant,actor,slot))
                    q.Text(4,op).Integer(5,epoch).Integer(6,expected).Blob(7,ownedRequest)
                     .Integer(8,first).Integer(9,last).Blob(10,batch).Blob(11,ownedReply).Step();
                Sql("COMMIT");return new DurableCommandReceiptV1(first,last,ownedReply,false);
            }catch{Rollback();throw;}
        }
    }
    public void Dispose()
    {
        lock(_gate)
        {
            if(_db!=IntPtr.Zero){Native.sqlite3_close_v2(_db);_db=IntPtr.Zero;}
        }
    }
}
