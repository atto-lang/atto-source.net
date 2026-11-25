using System;
using Ores.Net.Runtime;
using static Ores.Net.Runtime.ActorDataCodecPolicyV1;

static void Rejected(Reject expected, Action call)
{
    try { call(); }
    catch (PolicyError ex) { if (ex.Reason != expected) throw; return; }
    throw new Exception("expected rejection: " + expected);
}
var h = new byte[32]; Array.Fill(h, (byte)7);
var b = Binding.Select(null,15,1,2,"order.v1",h);
b.RequireMatch("messagepack",2,"order.v1",h);
Binding.Select("protobuf",15,2,1,"s",h).RequireMatch("protobuf",1,"s",h);
Binding.Select("cbor",4,4,1,"s",h).RequireMatch("cbor",1,"s",h);
Binding.Select("flatbuffers",8,8,1,"s",h).RequireMatch("flatbuffers",1,"s",h);
Rejected(Reject.Denied,()=>Binding.Select(null,2,2,1,"s",h));
Rejected(Reject.Unsupported,()=>Binding.Select("protobuf",15,1,1,"s",h));
Rejected(Reject.UnknownCodec,()=>Binding.Select("CBOR",15,15,1,"s",h));
Rejected(Reject.UnknownCodec,()=>Binding.Select("",15,15,1,"s",h));
Rejected(Reject.InvalidMask,()=>Binding.Select(null,16,1,1,"s",h));
Rejected(Reject.InvalidMask,()=>Binding.Select(null,-1,1,1,"s",h));
Rejected(Reject.InvalidBinding,()=>Binding.Select(null,1,1,0,"s",h));
Rejected(Reject.InvalidBinding,()=>Binding.Select(null,1,1,0x100000000L,"s",h));
foreach (var s in new string?[] {null,"","a b","é",new string('x',257)})
    Rejected(Reject.InvalidBinding,()=>Binding.Select(null,1,1,2,s,h));
Binding.Select(null,1,1,2,new string('x',256),h);
Rejected(Reject.InvalidBinding,()=>Binding.Select(null,1,1,2,"s",new byte[31]));
Rejected(Reject.Mismatch,()=>b.RequireMatch("protobuf",2,"order.v1",h));
Rejected(Reject.Mismatch,()=>b.RequireMatch("messagepack",1,"order.v1",h));
Rejected(Reject.Mismatch,()=>b.RequireMatch("messagepack",2,"order.v2",h));
Rejected(Reject.Mismatch,()=>b.RequireMatch("MESSAGEPACK",2,"order.v1",h));
h[0]=8; Rejected(Reject.Mismatch,()=>b.RequireMatch("messagepack",2,"order.v1",h));
h[0]=7; b.RequireMatch("messagepack",2,"order.v1",h);
Console.WriteLine("ACTOR_DATA_CODEC_POLICY_V1_HOST_PASS");
