import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,rmSync,copyFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
const dir=mkdtempSync(join(tmpdir(),'atto-bounded-net-'));
const generator=join(dir,'generator'),guest=join(dir,'guest');
const golden='4154544f53543032025b193c24a8de14db0400010000000000000080020103080000004100c3a9f09f9880040300000000ff8074c4cc420e041ac0';
function run(exe,args,cwd,timeout=300000){const r=spawnSync(exe,args,{cwd,encoding:'utf8',timeout,maxBuffer:8<<20});assert.equal(r.error,undefined);assert.equal(r.signal,null);assert.equal(r.status,0,r.stdout+'\n'+r.stderr);return r.stdout.trim();}
try{
 mkdirSync(generator);mkdirSync(guest);
 const project=(name,aot)=>'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>'+name+'</AssemblyName><TreatWarningsAsErrors>true</TreatWarningsAsErrors><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'+(aot?'<PublishAot>true</PublishAot><InvariantGlobalization>true</InvariantGlobalization>':'')+'</PropertyGroup></Project>';
 writeFileSync(join(generator,'Generator.csproj'),project('generator',false));
 copyFileSync(fileURLToPath(new URL('../compiler/Ores.Net.Compiler.Core/DurableBoundedSourceV2.cs',import.meta.url)),join(generator,'DurableBoundedSourceV2.cs'));
 writeFileSync(join(generator,'Program.cs'),`using Ores.Net.Compiler;
using static Ores.Net.Compiler.DurableBoundedSourceV2;
Field[] fields={new("count",Kind.Int),new("open",Kind.Bool),new("name",Kind.String),new("payload",Kind.Bytes)};
var source=Emit("Ledger",fields);
if(source!=Emit("Ledger",fields))throw new Exception("nondeterministic");
void Reject(Action a){try{a();}catch(FormatException){return;}throw new Exception("invalid manifest accepted");}
Reject(()=>Emit("Bad\\\"",fields));Reject(()=>Emit("Ledger",new Field[]{new("x",Kind.Int),new("x",Kind.Bool)}));
File.WriteAllText(args[0],source);
`);
 run('dotnet',['run','-c','Release','--',join(guest,'Codec.g.cs')],generator);
 writeFileSync(join(guest,'Guest.csproj'),project('guest',true));
 writeFileSync(join(guest,'Program.cs'),`using System.Buffers.Binary;
static void Check(bool ok){if(!ok)throw new Exception("assert");}
static void Reject(Action a){try{a();}catch(FormatException){return;}throw new Exception("invalid accepted");}
static bool Same(object[] a,object[] b)=>a.Length==b.Length&&a.Zip(b).All(p=>p.First is byte[] x&&p.Second is byte[] y?x.SequenceEqual(y):Equals(p.First,p.Second));
static void Rehash(byte[] b){ulong h=0xcbf29ce484222325UL;foreach(byte x in b.AsSpan(0,b.Length-8))h=unchecked((h^x)*0x100000001b3UL);BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(b.Length-8),h);}
object[] v={long.MinValue,true,"A\\0é😀",new byte[]{0,255,128}};
byte[] b=AttoDurableBoundedV2.Encode(v);Check(Convert.ToHexString(b).ToLowerInvariant()=="${golden}");Check(Same(v,AttoDurableBoundedV2.Decode(b)));
for(long i=-5000;i<=5000;i++){object[] x={i,i%2==0,new string('a',(int)((i+5000)%257)),new byte[]{unchecked((byte)i),0,255}};Check(Same(x,AttoDurableBoundedV2.Decode(AttoDurableBoundedV2.Encode(x))));}
for(int i=0;i<b.Length;i++){int cut=i;Reject(()=>AttoDurableBoundedV2.Decode(b[..cut]));for(int bit=0;bit<8;bit++){byte[] bad=(byte[])b.Clone();bad[i]^=(byte)(1<<bit);Reject(()=>AttoDurableBoundedV2.Decode(bad));}}
foreach(int i in new[]{8,29,31,37}){byte[] bad=(byte[])b.Clone();bad[i]=255;Rehash(bad);Reject(()=>AttoDurableBoundedV2.Decode(bad));}
foreach(string s in new[]{"\\ud800","\\udc00",new string('a',16385),string.Concat(Enumerable.Repeat("😀",4097))})Reject(()=>AttoDurableBoundedV2.Encode(0L,false,s,new byte[0]));
Reject(()=>AttoDurableBoundedV2.Encode(0L,false,"",new byte[16385]));Reject(()=>AttoDurableBoundedV2.Decode(new byte[65537]));
Check(((string)AttoDurableBoundedV2.Decode(AttoDurableBoundedV2.Encode(0L,false,new string('a',16384),new byte[16384]))[2]).Length==16384);
((byte[])AttoDurableBoundedV2.Decode(b)[3])[0]=42;Check(Same(v,AttoDurableBoundedV2.Decode(b)));
Console.WriteLine("NET_NATIVEAOT_BOUNDED_V2_PASS roundtrips=10001 bitflips=472 truncations=59");
`);
 run('dotnet',['publish','Guest.csproj','-c','Release','-r','linux-x64','-o','native','-warnaserror'],guest);
 const app=join(guest,'native','guest');
 assert.deepEqual(readFileSync(app).subarray(0,4),Buffer.from([127,69,76,70]));
 assert.ok(!/libcoreclr|libhostfxr/i.test(run('ldd',[app],guest)));
 const result=run(app,[],guest);assert.equal(result,'NET_NATIVEAOT_BOUNDED_V2_PASS roundtrips=10001 bitflips=472 truncations=59');console.log(result);
}finally{rmSync(dir,{recursive:true,force:true});}
