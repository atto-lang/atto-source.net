// Execute the portable corpus through the real structured compiler and NativeAOT host.
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {readFileSync,writeFileSync,mkdtempSync,rmSync,existsSync,mkdirSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
const [checkoutArg,head,tree,manifestPath]=process.argv.slice(2);
if(!checkoutArg||!manifestPath||![head,tree].every(x=>typeof x==='string'&&/^[a-f0-9]{40}$/.test(x)))throw Error('usage: node runner CLEAN_CHECKOUT HEAD TREE MANIFEST');
const root=resolve(checkoutArg),hash=b=>createHash('sha256').update(b).digest('hex');
const run=(cmd,args,cwd=root,timeout=120000)=>spawnSync(cmd,args,{cwd,encoding:'utf8',timeout,maxBuffer:16*1024*1024});
const checked=(r,label)=>{if(r.error||r.signal||r.status!==0)throw Error(label+': '+(r.error?.message||r.stderr||r.stdout||r.signal||r.status));return r.stdout.trim()};
const git=(...args)=>checked(run('git',args),'git');
const verify=()=>{if(git('rev-parse','HEAD')!==head||git('rev-parse','HEAD^{tree}')!==tree||git('write-tree')!==tree||git('status','--porcelain','--untracked-files=all')!=='')throw Error('source identity or clean checkout mismatch')};
verify();
const bytes=readFileSync(resolve(manifestPath)),m=JSON.parse(bytes);
if(m.schema!=='atto.unicode-identifiers-negation.v1'||!Array.isArray(m.cases)||m.cases.length!==19||new Set(m.cases.map(c=>c.id)).size!==19)throw Error('invalid shared corpus');
const tmp=mkdtempSync(join(tmpdir(),'atto-net-unicode-')),rows=[];
try{
 const compilerProject=join(root,'compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj');
 const build=join(tmp,'compiler');
 checked(run('dotnet',['build',compilerProject,'-c','Release','-o',build],root,600000),'compiler build');
 const compiler=join(build,'oresnet.dll');
 const compilerSha256=hash(readFileSync(compiler));
 const host=join(root,'host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj');
 for(const c of m.cases){
  if(!/^[a-z0-9-]+$/.test(c.id)||typeof c.source!=='string'||!(Number.isSafeInteger(c.expect)||c.expect==='reject'&&typeof c.diagnostic==='string'))throw Error('invalid fixture');
  const dir=join(tmp,c.id);mkdirSync(dir);const source=join(dir,'source.atto'),out=join(dir,'OresGenerated.dll'),publish=join(dir,'published');
  writeFileSync(source,c.source,'utf8');
  const r=run('dotnet',[compiler,'compile-structured',source,'--out',out]);
  const row={id:c.id,sourceSha256:hash(readFileSync(source)),expected:c.expect,compilerExit:r.status,artifact:existsSync(out),status:'FAIL'};
  try{
   if(r.error||r.signal)throw Error('compiler infrastructure failure');
   if(c.expect==='reject'){
    const diagnostic=r.stderr.trim();
    const stray=['postfix','composite-eq','composite-equal','unicode-not-not-a-bang-keyword'].includes(c.id)&&/expected.*;|expected.*semicolon/i.test(diagnostic);
    // Preserve the exact-source oracle. A distinct companion fixture exercises
    // the operator in a well-formed entrypoint profile; never counts as a
    // replacement for the original case's diagnostic.
    const helperIds=['postfix','composite-eq','composite-equal'];
    let witness=null;
    if(helperIds.includes(c.id)){
      witness=c.source.replace('pub fnc main(bool b): bool','fnc probe(bool b): bool')+
        ' pub fnc main(): int { return 42; }';
    }else if(c.id==='unicode-not-not-a-bang-keyword'){
      witness='pub fnc main(): int { val int copy = 1; return copy¬ 42; }';
    }
    if(witness!==null){
      const witnessSource=join(dir,'operator-witness.atto'), witnessOut=join(dir,'operator-witness.dll');
      writeFileSync(witnessSource,witness,'utf8');
      const w=run('dotnet',[compiler,'compile-structured',witnessSource,'--out',witnessOut]);
      const wDiagnostic=w.stderr.trim();
      const passed=!w.error&&!w.signal&&w.status===1&&!existsSync(witnessOut)&&
        /Expected ';' at offset [0-9]+$/i.test(wDiagnostic);
      row.operatorWitness={sourceSha256:hash(readFileSync(witnessSource)),compilerExit:w.status,
        artifact:existsSync(witnessOut),diagnostic:wDiagnostic,status:passed?'PASS':'FAIL'};
      console.log('UNICODE_OPERATOR_WITNESS '+JSON.stringify({id:c.id,...row.operatorWitness}));
      if(!passed)throw Error('operator witness failed: '+wDiagnostic);
    }
    if(r.status!==1||row.artifact||!diagnostic||(!stray&&!new RegExp(c.diagnostic,'i').test(diagnostic))||/entrypoint|main must have signature|file not found|could not execute|Unhandled exception|unsupported feature/i.test(diagnostic))throw Error('unexpected rejection: '+diagnostic);
    row.diagnostic=diagnostic;
   }else{
    if(r.status!==0||!row.artifact)throw Error('compiler failed: '+r.stderr);
    row.cilSha256=hash(readFileSync(out));
    checked(run('dotnet',['publish',host,'-c','Release','-r','linux-x64','-o',publish,'-p:GeneratedAssembly='+out,'-warnaserror','-t:Rebuild'],root,600000),'NativeAOT publish');
    const app=join(publish,'oreslang-app'),bin=readFileSync(app);
    if(!bin.subarray(0,4).equals(Buffer.from([0x7f,0x45,0x4c,0x46]))||bin[4]!==2||existsSync(join(publish,'OresGenerated.dll'))||existsSync(join(publish,'oreslang-app.runtimeconfig.json')))throw Error('not standalone ELF64');
    row.guestSha256=hash(bin);
    row.actual=checked(run(app,[],dir,20000),'guest execution');
    if(row.actual!==String(c.expect))throw Error('incorrect guest result');
   }
   row.status='PASS';
  }catch(e){row.failure=e.message}
  rows.push(row);console.log('UNICODE_CASE '+JSON.stringify(row));
 }
 verify();
 console.log(JSON.stringify({schema:'atto.unicode-net-nativeaot-execution.v1',compilerHead:head,compilerTree:tree,compilerSha256,corpusSha256:hash(bytes),rows},null,2));
 if(rows.some(r=>r.status!=='PASS'))process.exitCode=1;
}finally{rmSync(tmp,{recursive:true,force:true})}
