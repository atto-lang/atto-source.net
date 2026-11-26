import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {existsSync,mkdtempSync,readFileSync,rmSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
assert.ok(process.env.ATTO_NET_COMPILER);
const cli=resolve(process.env.ATTO_NET_COMPILER),dir=mkdtempSync(join(tmpdir(),'atto-net-scalar-'));
const source='define durable actor Ledger as state balance: int = -9223372036854775808; state open: bool = true; state last: int = 9223372036854775807; end';
const golden='4154544f5354303101f9b8eff04a5474590300010000000000000080020101ffffffffffffff7f4191e1156e4df645';
function run(command,args,timeout=30000,ok=true){const r=spawnSync(command,args,{cwd:dir,encoding:'utf8',timeout,maxBuffer:8<<20});assert.equal(r.error,undefined);assert.equal(r.signal,null);if(ok)assert.equal(r.status,0,r.stdout+'\n'+r.stderr);else assert.notEqual(r.status,0);return r.stdout.trim();}
const input=join(dir,'ledger.atto'),output=join(dir,'Codec.g.cs');
function emit(s,ok=true){writeFileSync(input,s);rmSync(output,{force:true});run(cli,['emit-durable-scalar-codec-v1',input,'--out',output],30000,ok);assert.equal(existsSync(output),ok);return ok?readFileSync(output,'utf8'):null;}
try{
 const first=emit(source);assert.equal(emit(source),first);
 const fields=Array.from({length:256},(_,i)=>`state f${i}: int = ${i};`).join(' ');
 for(const s of [source.replace('durable','persistent'),source.replace('balance','self'),source.replace('state last','state balance'),source.replace('-9223372036854775808','-9223372036854775809'),source.replace('9223372036854775807','9223372036854775808'),source.replace('bool = true','bool = 1'),source.replace('bool = true','Socket = true'),source+' //comment',source+'\u0000',source+' fnc main(): int {return 0;}',source.replace('Ledger','"Ledger"'),source.replace('int = -9223372036854775808','int = 00'),'define durable actor Maximum as '+fields+' state extra: bool = true; end'])emit(s,false);
 const vectors=[
 ['define durable actor Counter as state count: int = 100; state enabled: bool = true; end','4154544f5354303101c9a1af17fc8684bd020001640000000000000002018fd7fcf2f2744e29'],
 ['define durable actor Counter as state count: int = -9223372036854775808; state enabled: bool = false; end','4154544f5354303101c9a1af17fc8684bd0200010000000000000080020058ebbfc357901b30'],
 ['define durable actor EmptyActor as end','4154544f5354303101c3fbc65066197bff0000b3c015f0f5029da5'],
 ['define durable actor OtherCounter as state count: int = 100; end','4154544f53543031010201d7f4b9c36b6c0100016400000000000000eb0202552d636527'],
 ['define durable actor Maximum as '+fields+' end',null]];
 // Distinct class names for harness only; no semantic code rewrite.
 for(let i=0;i<vectors.length;i++){const generated=emit(vectors[i][0]);writeFileSync(join(dir,'Vector'+i+'.cs'),generated.replace('class AttoDurableScalarV1','class Vector'+i));}
 emit(source);
 writeFileSync(join(dir,'Guest.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>guest</AssemblyName><PublishAot>true</PublishAot><SelfContained>true</SelfContained><InvariantGlobalization>true</InvariantGlobalization><TreatWarningsAsErrors>true</TreatWarningsAsErrors><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>');
 writeFileSync(join(dir,'Program.cs'),`using System.Buffers.Binary;
static void Check(bool x){if(!x)throw new Exception("assert");}
static void Reject(Action action){try{action();}catch(FormatException){return;}throw new Exception("accepted invalid input");}
static void Rehash(byte[] b){ulong h=0xcbf29ce484222325UL;foreach(byte x in b.AsSpan(0,b.Length-8))h=unchecked((h^x)*0x100000001b3UL);BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(b.Length-8),h);}
byte[] b=AttoDurableScalarV1.Initial();
Check(Convert.ToHexString(b).ToLowerInvariant()=="${golden}");
Check(AttoDurableScalarV1.Decode(b).SequenceEqual(new long[]{long.MinValue,1,long.MaxValue}));
for(long i=-5000;i<=5000;i++){long[] v={i,i%2==0?1:0,-i};Check(AttoDurableScalarV1.Decode(AttoDurableScalarV1.Encode(v)).SequenceEqual(v));}
for(int i=0;i<b.Length;i++){int n=i;Reject(()=>AttoDurableScalarV1.Decode(b[..n]));for(int bit=0;bit<8;bit++){byte[] bad=(byte[])b.Clone();bad[i]^=(byte)(1<<bit);Reject(()=>AttoDurableScalarV1.Decode(bad));}}
byte[] corrupt=(byte[])b.Clone();corrupt[29]=2;Rehash(corrupt);Reject(()=>AttoDurableScalarV1.Decode(corrupt));
Reject(()=>AttoDurableScalarV1.Decode(new byte[b.Length+1]));Reject(()=>AttoDurableScalarV1.Encode(0,2,0));Reject(()=>AttoDurableScalarV1.Encode(0,1));b[0]=0;Check(Convert.ToHexString(AttoDurableScalarV1.Initial()).ToLowerInvariant()=="${golden}");
${vectors.slice(0,4).map((v,i)=>`Check(Convert.ToHexString(Vector${i}.Initial()).ToLowerInvariant()=="${v[1]}");Check(Vector${i}.Encode(Vector${i}.Decode(Vector${i}.Initial())).SequenceEqual(Vector${i}.Initial()));`).join('\n')}
Check(Vector4.Initial().Length==2331);Check(Vector4.Decode(Vector4.Initial()).SequenceEqual(Enumerable.Range(0,256).Select(x=>(long)x)));
Console.WriteLine("NET_NATIVEAOT_SCALAR_CODEC_PASS roundtrips=10001 bitflips=376 shared_golden=4 max_fields=256 rejected_sources=13");
`);
 run('dotnet',['publish','Guest.csproj','-c','Release','-r','linux-x64','-o','native','-warnaserror'],300000);
 const app=join(dir,'native','guest');assert.deepEqual(readFileSync(app).subarray(0,4),Buffer.from([127,69,76,70]));assert.ok(!/libcoreclr|libhostfxr/i.test(run('ldd',[app])));
 assert.equal(run(app,[]),'NET_NATIVEAOT_SCALAR_CODEC_PASS roundtrips=10001 bitflips=376 shared_golden=4 max_fields=256 rejected_sources=13');console.log(run(app,[]));
}finally{rmSync(dir,{recursive:true,force:true});}
