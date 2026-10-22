import {readFileSync} from "node:fs";
import {pathToFileURL} from "node:url";
// Source-wiring regression guard, not native lifetime or compiler proof.
export function validateArchitecture({runtime,arena,shard}) {
 const errors=[],req=(pass,why)=>{if(!pass)errors.push(why);};
 for(const proto of ["ActorNativeArena","ActorNativeHeap"]) {
  req(!new RegExp("\\b"+proto+"\\b").test(runtime),"runtime must not reference "+proto);
  req(!new RegExp("\\b"+proto+"\\b").test(shard),"shard must not reference "+proto);
 }
 req(runtime.includes("arena = new ActorArena("),"Spawn must own canonical ActorArena");
 req(runtime.includes("private readonly ActorArena _arena;"),"ActorContext must retain ActorArena");
 req(runtime.includes("cell.Arena.EnterTurn(") && runtime.includes("cell.Arena.Retire();"),
   "actor dispatch and termination must enter/retire arena");
 for(const token of ["ReferenceEquals(_activeTurn, turn)",
                     "ReferenceEquals(_activeCarrier, Thread.CurrentThread)"])
  req(arena.split(token).length-1>=2,"release and read require two checks for "+token);
 req(arena.includes("ActorArenaBudget.Process.Reserve(") &&
     arena.includes("ActorArenaBudget.Process.Refund("),"process cap reservation/refund required");
 req(arena.includes("ReferenceEqualityComparer.Instance"),"exact token issuance required");
 for(const layout of ["Aos","Soa","Aosoa","Soaos"])
  req(shard.includes("ActorDataLayout."+layout),"layout "+layout+" absent");
 req(shard.includes("arena.AllocatePersistent(turn,"),"shard must allocate in ActorArena");
 const gate=shard.indexOf("if (soaosGroupWidths.Length > fields)");
 const clone=shard.indexOf("soaosGroupWidths.Clone()");
 req(gate>=0 && clone>=0 && gate<clone,"SoAoS groups must be bounded before clone");
 return errors;
}
export function readSources(root=".") {
 const get=name=>readFileSync(root+"/runtime/Ores.Net.Runtime/"+name+".cs","utf8");
 return {runtime:get("ActorRuntime"),arena:get("ActorArena"),shard:get("ActorShardColumns")};
}
if(process.argv[1] && import.meta.url===pathToFileURL(process.argv[1]).href) {
 const issues=validateArchitecture(readSources());
 if(issues.length) { for(const e of issues)console.error("DENIED: "+e);process.exitCode=1; }
 else console.log("ACTOR_ARCH_SOURCE_GUARD_PASS");
}
