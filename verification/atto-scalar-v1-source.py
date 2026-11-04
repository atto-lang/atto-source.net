#!/usr/bin/env python3
"""19 exact .atto fixture hash check; this host code NEVER executes Atto guests."""
import hashlib
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
MANIFEST_BLOB="43c4d295a237e483f70722bd90ec10d273b73854"
def require(ok,why):
    if not ok: raise RuntimeError(why)
def blob(data):
    return hashlib.sha1(b"blob "+str(len(data)).encode()+b"\0"+data).hexdigest()
def distinct(pairs):
    out={}
    for k,v in pairs:
        require(k not in out,"duplicate JSON manifest field")
        out[k]=v
    return out
def file(rel):
    pieces=Path(rel).parts
    require(rel.startswith("contracts/atto-scalar-v1/") and
            all(x not in ("",".","..") for x in pieces) and
            "\\" not in rel,"unsafe Atto fixture path")
    path=ROOT
    for x in pieces:
        path=path/x
        require(not path.is_symlink(),"symlinked scalar fixture")
    require(path.is_file(),"missing scalar fixture")
    return path
def main():
    raw=file("contracts/atto-scalar-v1/cases.json").read_bytes()
    require(blob(raw)==MANIFEST_BLOB,"reviewed manifest SHA changed")
    c=json.loads(raw.decode("utf-8"),object_pairs_hook=distinct)
    require(c.get("schema")=="attolang.scalar-core.v1" and
            c.get("evidence_status")=="NOT_EXECUTED" and
            c.get("compiler_routes")==["java","llvm","wasm","net","forge"],
            "wrong five-backend source-only policy")
    cases=c.get("cases",[])
    require(len(cases)==19 and sum(x["kind"]=="positive" for x in cases)==10 and
            sum(x["kind"]=="negative" for x in cases)==9,
            "incomplete positive/negative corpus")
    for case in cases:
        expect="contracts/atto-scalar-v1/"+case["kind"]+"/"+case["id"]+".atto"
        require(case["path"]==expect,"ambiguous scalar source path")
        data=file(expect).read_bytes()
        require(0<len(data)<=8192 and blob(data)==case["git_blob_sha1"],
                "source bytes differ from canonical Git blob: "+case["id"])
    print("NET_ATTO_SCALAR_V1_SOURCE_ONLY_19")

if __name__=="__main__":main()
