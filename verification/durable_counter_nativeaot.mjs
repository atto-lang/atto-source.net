import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {existsSync,mkdtempSync,readFileSync,rmSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
const cli=resolve(process.env.ATTO_NET_COMPILER||'artifacts/compiler/linux-x64/oresnet');
const dir=mkdtempSync(join(tmpdir(),'atto-net-durable-'));
const source=`define durable actor Counter as
    state count: int = 0;
    receive(ActorMail<int> mail) -> void {
        self.count = self.count + mail.value;
    }
end
`;
function run(command,args,input,timeout=30000){
  const r=spawnSync(command,args,{cwd:dir,encoding:'utf8',input,timeout,maxBuffer:8<<20});
  assert.equal(r.error,undefined);return r;
}
function emit(text,mode=true,ok=true){
  const input=join(dir,'counter.atto'),output=join(dir,mode?'Counter.g.cs':'OresGenerated.dll');
  writeFileSync(input,text);rmSync(output,{force:true});
  const r=run(cli,[mode?'emit-durable-counter-v1':'compile',input,'--out',output]);
  assert.equal(r.status,ok?0:1,r.stderr);
  assert.equal(existsSync(output),ok);
  if(!ok){assert.ok(r.stderr.trim());return;}
  return readFileSync(output);
}
function descriptor(field){
  function fnv(bytes){let h=0xcbf29ce484222325n;for(const b of bytes)h=BigInt.asUintN(64,(h^BigInt(b))*0x100000001b3n);return h;}
  function le(n,size){const b=Buffer.alloc(size);for(let i=0;i<size;i++)b[i]=Number((BigInt(n)>>BigInt(8*i))&255n);return b;}
  const actor=Buffer.from('Counter'),name=Buffer.from(field);
  const id=fnv(Buffer.concat([Buffer.from('atto.actor.state.schema.v1'),Buffer.from([0]),le(actor.length,4),actor,le(name.length,4),name,Buffer.from([1])]));
  const data=Buffer.concat([Buffer.from('ATTOST01'),Buffer.from([1]),le(id,8),le(1,2),Buffer.from([1]),le(0,8)]);
  const checkpoint=Buffer.concat([data,le(fnv(data),8)]);
  return Buffer.concat([Buffer.from([1,actor.length]),actor,Buffer.from([name.length]),name,Buffer.from([1,checkpoint.length]),checkpoint]);
}
try{
  const first=emit(source);assert.deepEqual(first,emit(source));
  assert.notDeepEqual(first,emit(source.replaceAll('count','balance').replaceAll('mail','event')));
  for(const bad of [
    source.replace('durable actor','actor'),source.replace('durable actor','persistent actor'),
    source.replace('= 0;','= 1;'),source.replace('state count','pub state count'),
    source.replace('self.count + mail.value','self.count - mail.value'),
    source.replace('mail.value','9'),source.replace('ActorMail<int>','ActorMail<bool>'),
    source.replace('self.count =','self.unknown ='),source.replace('receive(','pub receive('),
    source.replace('self.count = self.count + mail.value;','self.count = self.count + mail.value; self.count = 0;'),
    source+'\npub fnc main()->int {return 0;}',source+'//\u0000',source.replaceAll('count','self')
  ])emit(bad,true,false);
  emit(source+'\npub fnc main(): int {return 0;}',false,false);
  emit(source);
  // A native published test driver references the actual generated source.
  // No Reflection.Emit, interpreter, CLR-only execution, or guest DB authority.
  writeFileSync(join(dir,'Guest.csproj'),`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>guest</AssemblyName>
    <PublishAot>true</PublishAot><SelfContained>true</SelfContained><InvariantGlobalization>true</InvariantGlobalization>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors><ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup></Project>`);
  writeFileSync(join(dir,'Program.cs'),`using System.Globalization;
Console.WriteLine(Convert.ToHexString(AttoDurableCounterV1.Descriptor()).ToLowerInvariant());
string? line;
while ((line=Console.ReadLine()) is not null) {
    var p=line.Split(' ');
    if(p.Length!=3)return 3;
    Console.WriteLine(AttoDurableCounterV1.Transition(long.Parse(p[0],CultureInfo.InvariantCulture),long.Parse(p[1],CultureInfo.InvariantCulture),long.Parse(p[2],CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
}
return 0;
`.replace('string? line;','string line;').replace('string line;','var line = "";'));
  const build=run('dotnet',['publish','Guest.csproj','-c','Release','-r','linux-x64','-o','native','-warnaserror'],undefined,180000);
  assert.equal(build.status,0,build.stdout+'\n'+build.stderr);
  const app=join(dir,'native','guest');
  const magic=readFileSync(app).subarray(0,4);assert.deepEqual(magic,Buffer.from([127,69,76,70]));
  const file=run('file',[app]);assert.equal(file.status,0,file.stderr);assert.match(file.stdout,/ELF/);
  const ldd=run('ldd',[app]);assert.ok(!/libcoreclr|libhostfxr/i.test(ldd.stdout+ldd.stderr));
  const vectors=[],expected=[];
  const add=(s,o,d)=>{vectors.push([s,o,d]);expected.push(s<0n||s>1000000000n?-1n:o===2n?(d===0n?s:-1n):o!==1n||d<=0n||d>1000000000n||s+d>1000000000n?-1n:s+d);};
  for(let i=0n;i<100n;i++)add(i,1n,1n);
  for(const s of [-1n,0n,100n,999999999n,1000000000n,1000000001n])
    for(const o of [0n,1n,2n,3n])
      for(const d of [-1n,0n,1n,42n,1000000000n,1000000001n])add(s,o,d);
  const guest=run(app,[],vectors.map(v=>v.join(' ')).join('\n')+'\n');
  assert.equal(guest.status,0,guest.stderr);assert.equal(guest.stderr,'');
  const [hex,...values]=guest.stdout.trim().split('\n');
  assert.equal(hex,descriptor('count').toString('hex'));
  assert.equal(values.length,244);
  assert.deepEqual(values.map(BigInt),expected);
  console.log('NET_NATIVEAOT_DURABLE_SCHEMA_AND_244_TRANSITIONS_PASS');
}finally{rmSync(dir,{recursive:true,force:true});}
