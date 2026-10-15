import json, pathlib, subprocess, sys, tempfile
dotnet, compiler = sys.argv[1:]
cases = json.loads(pathlib.Path(__file__).with_name("mandatory-async-io.json").read_text())["cases"]
with tempfile.TemporaryDirectory() as directory:
 source=pathlib.Path(directory)/"main.ores";output=pathlib.Path(directory)/"OresGenerated.dll"
 for case in cases:
  source.write_text(case["source"])
  for command in [["check",str(source)],["compile",str(source),"--out",str(output)]]:
   output.write_bytes(b"preserve previous artifact")
   result=subprocess.run([dotnet,compiler,*command],capture_output=True,text=True,timeout=10)
   if case["expect"]=="accept":assert result.returncode==0,(case["id"],result)
   else:
    assert result.returncode==1 and not result.stdout and "oresnet:" in result.stderr,(case["id"],result)
    assert output.read_bytes()==b"preserve previous artifact",case["id"]
 source.write_text("pub fnc main():int{return 42;}")
 for flag in ["--core-io=legacy-blocking","--core-io=off","--allow-blocking-io","--core-io=native-async-only"]:
  result=subprocess.run([dotnet,compiler,"check",str(source),flag],capture_output=True,text=True,timeout=10)
  assert result.returncode==2 and not result.stdout,(flag,result)
print(f"{len(cases)} mandatory async cases checked in both .NET CLI modes; rejected compilations preserve artifacts")
