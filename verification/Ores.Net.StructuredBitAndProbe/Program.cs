using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Ores.Net.Compiler;

string root=Path.Combine(Path.GetTempPath(),"atto-bitand-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed=0;
try
{
    foreach(var (name,body,expected) in new (string,string,long)[]{
        ("literal","return 12 & 10;",8),
        ("negative","return (0 - 1) & 42;",42),
        ("zero","return 0 & 9223372036854775807;",0),
        ("minimum","return (0 - 9223372036854775807 - 1) & (0 - 1);",long.MinValue),
        ("precedence","return 12 & 10 ^^ 2;",10),
        ("additive","return 1 + 3 & 6;",4),
        ("const","const int x = 12 & 10; return x;",8),
        ("local","val int x = 12; val int y = 10; return x & y;",8),
        ("lazy-bool","if false && (1 / 0 == 1) then return 0; fi return 42;",42),
        ("grouped","if (12 & 10) == 8 then return 42; fi return 0;",42),
        ("helper","return mask(12,10);",8)
    })
    {
        string prefix=name=="helper"?"pub fnc mask(int a,int b):int { return a & b; }\n":"";
        string input=Write(name,prefix+"pub fnc main(): int { "+body+" }");
        var compiled=OresSourceModuleCompiler.Compile(input,new[]{root});
        string dll=Path.Combine(root,name+".dll");
        CilEmitter.EmitLibrary(compiled.Module,dll);
        var alc=new AssemblyLoadContext("bitand-"+name,true);
        try
        {
            using var stream=File.OpenRead(dll);
            var run=alc.LoadFromStream(stream).GetTypes().Single(t=>t.Name=="OresProgram").GetMethod("Run",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
            if(run is null||Convert.ToInt64(run.Invoke(null,null),CultureInfo.InvariantCulture)!=expected)
                throw new InvalidOperationException("bad compiled value "+name);
        }
        finally {alc.Unload();}
        passed++;
    }
    foreach(var (name,body) in new[]{
        ("bool","return true & false;"),
        ("mixed","return 12 & true;"),
        ("equality","return 12 & 10 == 8;"),
        ("missing","return 12 &;"),
        ("trivia","return 12 & & 10;")
    })
    {
        string input=Write(name,"pub fnc main(): int { "+body+" }");
        bool denied=false;
        try{_ = OresSourceModuleCompiler.Compile(input,new[]{root});}
        catch(FormatException){denied=true;}
        if(!denied)throw new InvalidOperationException("unexpected admission "+name);
        passed++;
    }
    Console.WriteLine("STRUCTURED_BITAND_CIL_PASS cases="+passed);
}
finally {Directory.Delete(root,true);}
string Write(string name,string source)
{
    string path=Path.Combine(root,name+".atto");
    File.WriteAllText(path,source);
    return path;
}
