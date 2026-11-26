using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Text;

namespace Ores.Net.Compiler;

// Same bounded ZIP32 transport contract as Java. No extraction.
internal static class FrozenZipDirectory
{
    internal sealed record Entry(uint Crc,uint Compressed,uint Size);
    private readonly record struct Span(int Start,int End);
    private const uint Local=0x04034b50,Central=0x02014b50,End=0x06054b50,Descriptor=0x08074b50;
    private static FormatException Bad()=>new("E_IMPORT_PATH: invalid frozen ZIP directory");
    private static void Range(int at,int length,int end) {
        if(at<0||length<0||(long)at+length>end)throw Bad();
    }
    private static ushort U16(byte[] b,int at)=>BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at,2));
    private static uint U32(byte[] b,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at,4));
    private static bool Fields(byte[] b,int at,Entry entry)=>U32(b,at)==entry.Crc&&U32(b,at+4)==entry.Compressed&&U32(b,at+8)==entry.Size;
    internal static IReadOnlyDictionary<string,Entry> Inspect(byte[] zip) {
        int end=zip.Length-22;
        Range(end,22,zip.Length);
        if(U32(zip,end)!=End||U16(zip,end+4)!=0||U16(zip,end+6)!=0||U16(zip,end+20)!=0)throw Bad();
        int count=U16(zip,end+10);
        long centralSize=U32(zip,end+12),centralOffset=U32(zip,end+16);
        if(count<1||count>66||U16(zip,end+8)!=count||centralOffset+centralSize!=end)throw Bad();
        int central=(int)centralOffset,at=central;
        var entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        var spans=new List<Span>();
        for(int i=0;i<count;i++) {
            Range(at,46,end);
            if(U32(zip,at)!=Central||U16(zip,at+34)!=0)throw Bad();
            int flags=U16(zip,at+8),method=U16(zip,at+10),n=U16(zip,at+28),extra=U16(zip,at+30),comment=U16(zip,at+32);
            if((flags&~0x080e)!=0||(method!=0&&method!=8)||(method==0&&(flags&14)!=0)||n<1||n>256)throw Bad();
            Range(at+46,n+extra+comment,end);
            for(int k=at+46;k<at+46+n;k++)if(zip[k]<32||zip[k]>126)throw Bad();
            string name=Encoding.ASCII.GetString(zip,at+46,n);
            var entry=new Entry(U32(zip,at+16),U32(zip,at+20),U32(zip,at+24));
            long localOffset=U32(zip,at+42);
            if(localOffset>central||entry.Compressed>central||!entries.TryAdd(name,entry))throw Bad();
            int local=(int)localOffset;
            Range(local,30,central);
            if(U32(zip,local)!=Local||U16(zip,local+6)!=flags||U16(zip,local+8)!=method||U16(zip,local+26)!=n)throw Bad();
            int localExtra=U16(zip,local+28);
            Range(local+30,n+localExtra,central);
            if(!zip.AsSpan(at+46,n).SequenceEqual(zip.AsSpan(local+30,n)))throw Bad();
            int data=local+30+n+localExtra;
            Range(data,(int)entry.Compressed,central);
            int after=data+(int)entry.Compressed;
            if((flags&8)==0) {
                if(!Fields(zip,local+14,entry))throw Bad();
            } else {
                Range(after,12,central);
                if(U32(zip,after)==Descriptor) {
                    Range(after,16,central);
                    if(!Fields(zip,after+4,entry))throw Bad();
                    after+=16;
                } else {
                    if(!Fields(zip,after,entry))throw Bad();
                    after+=12;
                }
                for(int field=0;field<3;field++) {
                    uint value=U32(zip,local+14+4*field);
                    if(value!=0&&value!=U32(zip,at+16+4*field))throw Bad();
                }
            }
            spans.Add(new Span(local,after));
            at+=46+n+extra+comment;
        }
        if(at!=end)throw Bad();
        spans.Sort((a,b)=>a.Start.CompareTo(b.Start));
        int cursor=0;
        foreach(var span in spans) {
            if(span.Start!=cursor||span.End<=cursor)throw Bad();
            cursor=span.End;
        }
        if(cursor!=central)throw Bad();
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string,Entry>(entries);
    }
    private static readonly uint[] CrcTable=MakeTable();
    private static uint[] MakeTable() {
        var table=new uint[256];
        for(int i=0;i<table.Length;i++) {
            uint value=(uint)i;
            for(int bit=0;bit<8;bit++)value=(value&1)!=0?0xedb88320U^(value>>1):value>>1;
            table[i]=value;
        }
        return table;
    }
    internal static uint Crc32(byte[] data) {
        uint crc=0xffffffffU;
        foreach(byte b in data)crc=CrcTable[(int)((crc^b)&255)]^(crc>>8);
        return crc^0xffffffffU;
    }
}
