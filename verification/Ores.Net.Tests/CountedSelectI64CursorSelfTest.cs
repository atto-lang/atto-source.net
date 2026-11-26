using Ores.Net.Runtime;

internal static class CountedSelectI64CursorSelfTest
{
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception("counted select: " + reason);
    }
    private static void Round(CountedSelectI64Cursor cursor)
    { cursor.BeginRound(); Require(cursor.Pump(), "ready round commits"); }
    public static void Run()
    {
        var a = new SelectableI64Channel(2);
        var b = new SelectableI64Channel(2);
        a.TryWrite(10); a.TryWrite(30); b.TryWrite(20); b.TryWrite(40);
        var c = new CountedSelectI64Cursor(3, new object[] { a,b });
        Round(c); Round(c); Round(c);
        Require(c.Result().SequenceEqual(new long[]{10,20,30}), "fair exact three");
        Require(b.TryRead(out var fourth) && fourth == 40, "fourth not consumed");
        Require(a.PendingReaders+b.PendingReaders == 0 && !c.Cancel(), "terminal no waiter");

        var f = new SelectableI64Future();
        var g = new SelectableI64Future(); f.Resolve(21);
        c = new(2, new object[]{ f,f,g });
        Round(c); c.BeginRound();
        Require(!c.Pump() && g.PendingWaiters==1 && !c.Dirty,"pending Future");
        var producer = new Thread(() => g.Resolve(42)); producer.Start(); producer.Join();
        Require(c.Dirty && c.Pump() && c.Result().SequenceEqual(new long[]{21,42}),"identity retirement and cross-thread notification");
        Require(f.PendingWaiters+g.PendingWaiters==0,"no Future waiter leak");

        a = new(1); b = new(1); a.TryWrite(1); a.Close(); b.TryWrite(2);
        c = new(2,new object[]{a,a,b},false);
        Round(c); Round(c); Round(c);
        Require(c.Result().SequenceEqual(new long[]{1,2}),"EOF retires aliases, drains buffered data first");

        f = new(); c = new(1,new object[]{f}); c.BeginRound();
        Require(c.Cancel() && f.PendingWaiters==0 && f.Status==SelectableI64Future.FutureStatus.Pending,"cancel detaches without cancelling producer");
        Require(f.Resolve(42),"producer survives cancel");

        f = new(); f.Resolve(1); c = new(2,new object[]{f,f}); Round(c);
        Require(c.State==CountedSelectI64Cursor.CursorState.Failed && c.Failure?.Message=="SelectExhausted","no short batch");
        try { c.Result(); throw new Exception("partial result"); } catch(InvalidOperationException) {}
        Require(new CountedSelectI64Cursor(0,Array.Empty<object>()).Result().Length==0,"zero");
        a = new(1,1); c = new(2,new object[]{a,a});
        try { c.BeginRound(); throw new Exception("expected waiter quota failure"); } catch(InvalidOperationException) {}
        Require(a.PendingReaders==0 && c.State==CountedSelectI64Cursor.CursorState.Failed,"registration rollback");

        var foreign = false; c = new(1,new object[]{a});
        var t = new Thread(()=>{try{c.BeginRound();}catch(InvalidOperationException){foreign=true;}});
        t.Start(); t.Join();
        Require(foreign && a.PendingReaders==0,"foreign owner rejected");
        Require(c.Cancel(),"cancel ready");

        // Same mixed lexical-order cases as the Java counted runtime gate.
        a = new(2); a.TryWrite(20); a.TryWrite(30);
        f = new(); f.Resolve(10);
        c = new(2, new object[]{f,a}, false);
        Round(c); Round(c);
        Require(c.Result().SequenceEqual(new long[]{10,20}), "mixed lexical priority");
        Require(a.TryRead(out var remaining) && remaining == 30, "priority preserves unselected payload");

        a = new(2); b = new(2);
        a.TryWrite(10); a.TryWrite(40); b.TryWrite(30); b.TryWrite(50);
        f = new(); f.Resolve(20);
        c = new(4, new object[]{a,f,b,f});
        Round(c); Round(c); Round(c); Round(c);
        Require(c.Result().SequenceEqual(new long[]{10,20,30,40}), "mixed FAIR order and Future alias retirement");
        Require(b.TryRead(out remaining) && remaining == 50 && f.PendingWaiters == 0, "no extra commit or Future waiter");
        Require(a.PendingReaders + b.PendingReaders == 0, "no channel waiter");

        try { _ = new CountedSelectI64Cursor(1, new object[]{a,new object()}); throw new Exception("unsupported source accepted"); }
        catch (ArgumentException) {}
        Require(a.PendingReaders == 0, "unsupported source rejected before registration");
        Require(new CountedSelectI64Cursor(0, null!).Result().Length == 0, "zero ignores source list");

        a = new(1); b = new(2); var d = new SelectableI64Channel(1); f = new(); f.Resolve(99);
        c = new(3,new object[]{a,b,f,b,d}); Round(c);
        a.TryWrite(1); b.TryWrite(2); b.TryWrite(3); d.TryWrite(4);
        Round(c); Round(c);
        Require(c.Result().SequenceEqual(new long[]{99,2,4}),"exact surviving lexical alias fairness");
    }
}
