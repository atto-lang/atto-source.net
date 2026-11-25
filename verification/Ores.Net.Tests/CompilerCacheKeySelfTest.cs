using System;
using System.Linq;
using Ores.Net.Compiler;
internal static class CompilerCacheKeySelfTest
{
    private static string H(int n)=>string.Concat(Enumerable.Repeat(n.ToString("x2",System.Globalization.CultureInfo.InvariantCulture),32));
    internal static void Run()
    {
        const string expected="6d0658c41d4e45ede029ca0c7654333c5aa0bc117f3179f12832aa7b1d22b657";
        var deps=new[]{new CompilerCacheKey.Dependency("@org/project/b",H(7)),new CompilerCacheKey.Dependency("@org/project/c",H(8))};
        var b=new CompilerCacheKey.Input("typed-ir","@org/project/a","namespace","network",H(1),H(2),H(3),H(4),H(5),H(6),null,deps);
        static void Same(string expected,string actual){if(expected!=actual)throw new InvalidOperationException("cache key vector mismatch");}
        static void Different(string expected,string actual){if(expected==actual)throw new InvalidOperationException("cache key collision");}
        static void Deny(CompilerCacheKey.Input input){try{_=CompilerCacheKey.Encode(input);}catch(FormatException e)when(e.Message.StartsWith("E_CACHE_KEY:",StringComparison.Ordinal)){return;}throw new InvalidOperationException("malformed cache key accepted");}
        Same(expected,CompilerCacheKey.Encode(b));
        Same(expected,CompilerCacheKey.Encode(b with{Dependencies=deps.Reverse().ToArray()}));
        foreach(var changed in new[]{b with{Compiler=H(9)},b with{Toolchain=H(9)},b with{Target=H(9)},b with{Configuration=H(9)},b with{Sources=H(9)},b with{Bindings=H(9)},b with{Generated=H(0)},b with{Stage="native-code"},b with{Owner="@org/project/z"},b with{UnitKind="source-unit"},b with{Unit="other"},b with{Dependencies=Array.Empty<CompilerCacheKey.Dependency>()},b with{Dependencies=new[]{deps[0] with{Digest=H(9)},deps[1]}}})
            Different(expected,CompilerCacheKey.Encode(changed));
        Different(CompilerCacheKey.Encode(b with{Owner="ab",Unit="c"}),CompilerCacheKey.Encode(b with{Owner="a",Unit="bc"}));
        foreach(string bad in new[]{"","a\n","a\0b","\ud800",new string('x',513)})Deny(b with{Owner=bad});
        foreach(string bad in new[]{"",new string('F',64),H(1)+"\n"}){Deny(b with{Compiler=bad});Deny(b with{Generated=bad});}
        Deny(b with{UnitKind="package"});Deny(b with{Dependencies=new[]{deps[0],deps[0]}});
        Deny(b with{Dependencies=Enumerable.Repeat(deps[0],129).ToArray()});
        Deny(b with{Dependencies=new CompilerCacheKey.Dependency[]{null!}});
        Console.WriteLine("CACHE_KEY_NET|vector="+expected+"|permutation|field-drift|framing|malformed-rejected");
    }
}
