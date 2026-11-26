using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
namespace Ores.Net.Compiler;

// Candidate admission only. Current anchor and secret are independently
// provisioned by the protected host. No durable rollback prevention claim.
public static class CompilerCacheLedger
{
    private static readonly byte[] Magic=Encoding.ASCII.GetBytes("ATTOLED1");
    private static readonly byte[] Domain=Encoding.ASCII.GetBytes("atto.compiler.local-receipt-key.v1\0");
    private static FormatException Invalid()=>new("E_CACHE_LEDGER: invalid or stale record");
    private static byte[] Scope(string scope){
        if(scope is null||scope.Length is <1 or >512||scope.Any(c=>c<33||c>126))throw Invalid();
        return Encoding.ASCII.GetBytes(scope);
    }
    private static byte[] Derive(byte[] scope,ulong epoch,byte[] master){
        if(epoch==0||master is null||master.Length!=32||CryptographicOperations.FixedTimeEquals(master,new byte[32]))throw Invalid();
        var message=new byte[Domain.Length+4+scope.Length+8];
        Domain.CopyTo(message,0);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(Domain.Length,4),(uint)scope.Length);
        scope.CopyTo(message,Domain.Length+4);
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(Domain.Length+4+scope.Length),epoch);
        return HMACSHA256.HashData(master,message);
    }
    public static byte[] Record(string scope,ulong epoch,byte[] master){
        var id=Scope(scope);var record=new byte[52+id.Length];
        Magic.CopyTo(record,0);BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(8,4),(uint)id.Length);
        id.CopyTo(record,12);BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(12+id.Length,8),epoch);
        SHA256.HashData(Derive(id,epoch,master)).CopyTo(record,20+id.Length);return record;
    }
    public static byte[] Admit(string scope,byte[] record,byte[] currentPin,byte[] master){
        var id=Scope(scope);
        if(record is null||record.Length!=52+id.Length||currentPin is null||currentPin.Length!=32)throw Invalid();
        var bytes=(byte[])record.Clone();
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),currentPin)||
            !bytes.AsSpan(0,8).SequenceEqual(Magic)||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8,4))!=(uint)id.Length||
            !bytes.AsSpan(12,id.Length).SequenceEqual(id))throw Invalid();
        var key=Derive(id,BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12+id.Length,8)),master);
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(key),bytes.AsSpan(20+id.Length)))throw Invalid();
        return key;
    }
}
