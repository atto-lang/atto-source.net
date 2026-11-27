using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

static void Require(bool valid, string message)
{
    if (!valid) throw new InvalidOperationException(message);
}
static string ReadPinned(string path, string expected)
{
    byte[] bytes=File.ReadAllBytes(path);
    string blob=Convert.ToHexStringLower(SHA1.HashData(
        Encoding.ASCII.GetBytes("blob "+bytes.Length.ToString(CultureInfo.InvariantCulture)+"\0").Concat(bytes).ToArray()));
    Require(blob==expected,"source drift "+path);
    return Encoding.UTF8.GetString(bytes);
}
string source=string.Join("\n",new[]{
    ReadPinned("stdlib/policy/core/arena_limits.atto","5928e893a2ce1baadb0376a4ccb14f63249f77d4"),
    ReadPinned("stdlib/policy/alloc/arena_lifecycle.atto","e4795c6d4bd561be3d913cdc617d103d0e2828c8"),
    ReadPinned("tests/fixtures/arena-quota-lifecycle-guest.atto","2b1eb28f07b792f2727abe9a5691849f024e304a")
});
string root=Path.Combine(Path.GetTempPath(),"atto-arena-policy-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    const string marker="arena_end_offset(0, 0, 1, 0) != 0";
    Require(source.Split(marker,StringSplitOptions.None).Length==2,"mutation marker");
    Execute("good",source,100);
    Execute("wrong-oracle",source.Replace(marker,"arena_end_offset(0, 0, 1, 0) != 1",StringComparison.Ordinal),1);
    Console.WriteLine("NET_ARENA_POLICY_CIL_PASS assertions=56 wrong_oracle=1");
}
finally { Directory.Delete(root,true); }

void Execute(string name,string text,int expected)
{
    string input=Path.Combine(root,name+".atto");
    File.WriteAllText(input,text);
    var compiled=OresSourceModuleCompiler.Compile(input,new[]{root});
    string output=Path.Combine(root,name+".dll");
    CilEmitter.EmitLibrary(compiled.Module,output);
    var context=new AssemblyLoadContext("arena-policy-"+name,isCollectible:true);
    try
    {
        using var stream=File.OpenRead(output);
        var program=context.LoadFromStream(stream).GetTypes().Single(t=>t.Name=="OresProgram");
        var run=program.GetMethod("Run",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
        Require(run is not null && Convert.ToInt64(run.Invoke(null,null),CultureInfo.InvariantCulture)==expected,"compiled policy failed: "+name);
    }
    finally {context.Unload();}
}
