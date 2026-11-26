using System;
using System.Linq;
using System.Security.Cryptography;
using Ores.Net.Compiler;
internal static class CompilerArtifactReceiptSelfTest
{
    internal static void Run(){
        const string key="6d0658c41d4e45ede029ca0c7654333c5aa0bc117f3179f12832aa7b1d22b657";
        byte[] Secret()=>Enumerable.Range(0,32).Select(x=>(byte)x).ToArray();
        byte[] Payload()=>Convert.FromHexString("6174746f2d61727469666163742d763100ff");
        void Same(byte[] expected,byte[] actual){if(!expected.AsSpan().SequenceEqual(actual))throw new InvalidOperationException("receipt mismatch");}
        void Deny(Action action){try{action();}catch(FormatException e)when(e.Message.StartsWith("E_CACHE_RECEIPT:",StringComparison.Ordinal)){return;}throw new InvalidOperationException("invalid receipt accepted");}
        byte[] bytes=Payload(), sealedBytes=CompilerArtifactReceipt.Seal(key,bytes,Secret());
        Same(Convert.FromHexString("3653396c79c689b07bb08274d60b4de17d57d0c9dd4fd22180aa7af88940b524"),sealedBytes.AsSpan(80,32).ToArray());
        Same(bytes,CompilerArtifactReceipt.Verify(key,sealedBytes,Secret()));
        foreach(int i in new[]{0,8,40,48,80,112,sealedBytes.Length-1}){
            var changed=(byte[])sealedBytes.Clone();changed[i]^=1;
            Deny(()=>CompilerArtifactReceipt.Verify(key,changed,Secret()));
        }
        var forged=(byte[])sealedBytes.Clone();forged[112]^=1;
        SHA256.HashData(forged.AsSpan(112)).CopyTo(forged,48);
        Deny(()=>CompilerArtifactReceipt.Verify(key,forged,Secret()));
        Deny(()=>CompilerArtifactReceipt.Verify(new string('0',64),sealedBytes,Secret()));
        Deny(()=>CompilerArtifactReceipt.Verify(key,sealedBytes,new byte[32]));
        foreach(var bad in new[]{sealedBytes[..111],sealedBytes[..^1],sealedBytes.Concat(new byte[]{0}).ToArray(),new byte[112+CompilerArtifactReceipt.MaxArtifact+1]})
            Deny(()=>CompilerArtifactReceipt.Verify(key,bad,Secret()));
        Deny(()=>CompilerArtifactReceipt.Seal(key,bytes,new byte[31]));
        Deny(()=>CompilerArtifactReceipt.Seal(key,new byte[CompilerArtifactReceipt.MaxArtifact+1],Secret()));
        bytes[0]^=1;Same(Payload(),CompilerArtifactReceipt.Verify(key,sealedBytes,Secret()));
        var returned=CompilerArtifactReceipt.Verify(key,sealedBytes,Secret());returned[0]^=1;
        Same(Payload(),CompilerArtifactReceipt.Verify(key,sealedBytes,Secret()));
        Same(Array.Empty<byte>(),CompilerArtifactReceipt.Verify(key,CompilerArtifactReceipt.Seal(key,Array.Empty<byte>(),Secret()),Secret()));
        Console.WriteLine("CACHE_RECEIPT_NET|tag=3653396c79c689b07bb08274d60b4de17d57d0c9dd4fd22180aa7af88940b524|forgery-rejected|bounded|immutable");
    }
}
