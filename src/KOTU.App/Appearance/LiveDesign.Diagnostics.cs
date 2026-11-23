#if KOTU_DESIGN_TEST_HARNESS
using System.Diagnostics;

namespace KOTU.App.Appearance;

public sealed partial class LiveDesign
{
    // Only a harness readiness interval opts in. Hot paths allocate no event payloads.
    // These inclusive timings overlap (AttachTree -> Load -> Refresh); do not sum them
    // as exclusive UI utilization or infer a blocking cause from elapsed time alone.
    [ThreadStatic] private static DiagnosticWorkObservation? s_workObservation;
    internal enum DiagnosticWorkKind { AttachTree,Load,Refresh }
    internal sealed record DiagnosticWorkCounter(long Calls,double TotalMilliseconds,double MaxMilliseconds,long MaxStartedTimestamp,
        long MaxEndedTimestamp,string? MaxElementClrType);
    internal sealed record DiagnosticWorkSnapshot(long Timestamp,DiagnosticWorkCounter AttachTree,DiagnosticWorkCounter Load,DiagnosticWorkCounter Refresh);
    internal static DiagnosticWorkObservation BeginDiagnosticWorkObservation()=>new();
    private static DiagnosticWorkMeasurement MeasureDiagnosticWork(DiagnosticWorkKind kind,object element)=>new(s_workObservation,kind,element);

    internal readonly record struct DiagnosticGcSnapshot(long Timestamp,long TotalPauseTicks,int ManagedThreadId,
        int Generation0Collections,int Generation1Collections,int Generation2Collections,
        long ThreadAllocatedBytes,long ApproximateTotalAllocatedBytes);
    internal static DiagnosticGcSnapshot CaptureDiagnosticGcSnapshot()
    {
        // Read time/pause before any result objects are allocated. These process counters
        // are sequential samples, not one atomic snapshot or exclusive UI measurements.
        var timestamp=Stopwatch.GetTimestamp();
        var pauseTicks=GC.GetTotalPauseDuration().Ticks;
        var threadId=Environment.CurrentManagedThreadId;
        var generation0=GC.CollectionCount(0);
        var generation1=GC.CollectionCount(1);
        var generation2=GC.CollectionCount(2);
        var threadAllocated=GC.GetAllocatedBytesForCurrentThread();
        var totalAllocated=GC.GetTotalAllocatedBytes(precise:false);
        return new(timestamp,pauseTicks,threadId,generation0,generation1,generation2,threadAllocated,totalAllocated);
    }
    internal static object DiagnosticGcDifference(DiagnosticGcSnapshot before,DiagnosticGcSnapshot after)=>new {
        ElapsedMilliseconds=Stopwatch.GetElapsedTime(before.Timestamp,after.Timestamp).TotalMilliseconds,
        GcPauseMilliseconds=(after.TotalPauseTicks-before.TotalPauseTicks)/(double)TimeSpan.TicksPerMillisecond,
        Generation0Collections=after.Generation0Collections-before.Generation0Collections,
        Generation1Collections=after.Generation1Collections-before.Generation1Collections,
        Generation2Collections=after.Generation2Collections-before.Generation2Collections,
        SameManagedThread=before.ManagedThreadId==after.ManagedThreadId,
        ThreadAllocatedBytes=before.ManagedThreadId==after.ManagedThreadId ? (long?)(after.ThreadAllocatedBytes-before.ThreadAllocatedBytes) : null,
        ApproximateTotalAllocatedBytes=after.ApproximateTotalAllocatedBytes-before.ApproximateTotalAllocatedBytes,
        Scope="Sequential managed-runtime readings; process GC/collection/allocation counts include other threads and diagnostic work. Thread allocation delta is valid only for the same managed thread. Native allocations and scheduling delays are not measured." };

    internal sealed class DiagnosticWorkObservation : IDisposable
    {
        private readonly DiagnosticWorkObservation? _previous=s_workObservation;
        private readonly Counter[] _counters=new Counter[3];
        private bool _disposed;
        internal DiagnosticWorkObservation() { s_workObservation=this; }
        internal DiagnosticWorkSnapshot Snapshot()=>new(Stopwatch.GetTimestamp(),_counters[0].Snapshot(),_counters[1].Snapshot(),_counters[2].Snapshot());
        internal static object Difference(DiagnosticWorkSnapshot before,DiagnosticWorkSnapshot after)
        {
            static object Delta(DiagnosticWorkCounter first,DiagnosticWorkCounter last)=>new {
                Calls=last.Calls-first.Calls,TotalMilliseconds=last.TotalMilliseconds-first.TotalMilliseconds,
                last.MaxMilliseconds,last.MaxStartedTimestamp,last.MaxEndedTimestamp,last.MaxElementClrType };
            return new { ElapsedMilliseconds=Stopwatch.GetElapsedTime(before.Timestamp,after.Timestamp).TotalMilliseconds,
                InclusiveTimings=true,AttachTree=Delta(before.AttachTree,after.AttachTree),Load=Delta(before.Load,after.Load),
                Refresh=Delta(before.Refresh,after.Refresh) };
        }
        internal void Add(DiagnosticWorkKind kind,long started,long ended,Type? elementType)
        {
            if (!_disposed) _counters[(int)kind].Add(started,ended,elementType);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed=true;
            if (ReferenceEquals(s_workObservation,this)) s_workObservation=_previous;
        }
        private struct Counter
        {
            private long _calls,_ticks,_maximum,_maximumStarted,_maximumEnded;
            private Type? _maximumElementType;
            internal void Add(long started,long ended,Type? elementType)
            {
                var ticks=ended-started;
                _calls++;_ticks+=ticks;
                if (ticks>_maximum) { _maximum=ticks;_maximumStarted=started;_maximumEnded=ended;_maximumElementType=elementType; }
            }
            internal readonly DiagnosticWorkCounter Snapshot()=>new(_calls,1000d*_ticks/Stopwatch.Frequency,
                1000d*_maximum/Stopwatch.Frequency,_maximumStarted,_maximumEnded,_maximumElementType?.FullName);
        }
    }
    private readonly struct DiagnosticWorkMeasurement : IDisposable
    {
        private readonly DiagnosticWorkObservation? _owner;
        private readonly DiagnosticWorkKind _kind;
        private readonly long _started;
        private readonly Type? _elementType;
        internal DiagnosticWorkMeasurement(DiagnosticWorkObservation? owner,DiagnosticWorkKind kind,object element)
        {
            _owner=owner;_kind=kind;_started=owner is null ? 0 : Stopwatch.GetTimestamp();
            // CLR type only: never read UI properties or build per-call event payloads.
            _elementType=owner is null ? null : element.GetType();
        }
        public void Dispose()
        {
            if (_owner is not null) _owner.Add(_kind,_started,Stopwatch.GetTimestamp(),_elementType);
        }
    }
}
#endif
