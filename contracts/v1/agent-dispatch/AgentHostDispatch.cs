// HOST-ONLY contract. No Oreslang guest binding, issuer, or durable claim store.
// The embedding host must create the measured scope and supply trusted callbacks.
using System;
using System.Threading;
namespace Oreslang.AgentDispatchHost;
public sealed record Scope(string TenantId, string ActorId, long Generation,
    long Epoch, string Revision, string DispatchId, string ContextSha256,
    long ExpiresAtMs, long Utf8Bytes, long MeasuredTokens);
public sealed class TrustedHost {
    public required Func<Scope,bool> IsCurrent {get; init;}
    public required Func<Scope,bool> Authorize {get; init;}
    // Must implement an external durable unique constraint/atomic CAS, not an in-memory bool.
    public required Func<Scope,bool> ClaimOnce {get; init;}
    public required Func<Scope,bool> SubmitDataOnly {get; init;}
    public required Func<long> NowMs {get; init;} // trusted host clock
}
public sealed class AgentHostDispatchGate {
    private readonly string tenant, actor, revision;
    private readonly long generation, epoch, tokenCap;
    private int spent;
    public AgentHostDispatchGate(string tenant, string actor, long generation,
        long epoch, string revision, long tokenCap) {
        this.tenant=tenant; this.actor=actor; this.generation=generation;
        this.epoch=epoch; this.revision=revision; this.tokenCap=tokenCap;
    }
    private static bool Id(string? s) {
        if (string.IsNullOrEmpty(s) || s.Length>128) return false;
        foreach (char c in s)
            if (!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||
                c=='_'||c=='-'||c=='.'||c==':'||c=='@'||c=='/')) return false;
        return true;
    }
    private static bool Hex(string? s,int n) {
        if (s is null || s.Length!=n) return false;
        foreach(char c in s)
            if (!((c>='0'&&c<='9')||(c>='a'&&c<='f'))) return false;
        return true;
    }
    public bool Dispatch(Scope? s,TrustedHost? h,long nowMs) {
        if (Interlocked.Exchange(ref spent,1)!=0) return false;
        if (s is null || h is null || !Id(tenant) || !Id(actor) || !Id(s.DispatchId) ||
            s.TenantId!=tenant || s.ActorId!=actor || generation<=0 ||
            s.Generation!=generation || epoch<=0 || s.Epoch!=epoch ||
            !Hex(revision,40) || s.Revision!=revision ||
            !Hex(s.ContextSha256,64) || s.ExpiresAtMs<=nowMs ||
            s.Utf8Bytes<=0 || s.Utf8Bytes>65536 ||
            s.MeasuredTokens<=0 || s.MeasuredTokens>16384 ||
            tokenCap<=0 || s.MeasuredTokens>tokenCap ||
            h.IsCurrent is null || h.Authorize is null ||
            h.ClaimOnce is null || h.SubmitDataOnly is null ||
            h.NowMs is null) return false;
        try {
            var admittedAt=h.NowMs();
            if (admittedAt<0 || s.ExpiresAtMs<=admittedAt) return false;
            if (!h.IsCurrent(s) || !h.Authorize(s) || !h.ClaimOnce(s) ||
                !h.IsCurrent(s) || !h.Authorize(s)) return false;
            var beforeSubmit=h.NowMs();
            return beforeSubmit>=admittedAt && s.ExpiresAtMs>beforeSubmit &&
                   h.SubmitDataOnly(s);
        } catch(Exception) {return false;}
    }
}
