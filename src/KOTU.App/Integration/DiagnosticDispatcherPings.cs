#if KOTU_DESIGN_TEST_HARNESS
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace KOTU.App.Integration;

// The dispatcher is captured by the caller on the UI thread. The worker never receives
// a fixture, UI element or readiness predicate. A ping samples only clocks and process GC pause.
internal sealed class DiagnosticDispatcherPings : IDisposable
{
    private const int IntervalMilliseconds=100,MaximumPings=20,DeadlineMilliseconds=2000;
    private readonly DispatcherQueue _dispatcher;
    private readonly long _started=Stopwatch.GetTimestamp();
    private readonly ConcurrentQueue<Ping> _pings=new();
    private readonly CancellationTokenSource _cancellation=new();
    private readonly Task _worker;
    private long _workerStarted,_workerEnded;
    private int _stopped;
    private string? _workerError;
    internal DiagnosticDispatcherPings(DispatcherQueue dispatcher)
    {
        _dispatcher=dispatcher;
        _worker=Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        Interlocked.Exchange(ref _workerStarted,Stopwatch.GetTimestamp());
        try
        {
            for(var index=0;index<MaximumPings && Volatile.Read(ref _stopped)==0 &&
                Stopwatch.GetElapsedTime(_started).TotalMilliseconds<DeadlineMilliseconds;index++)
            {
                var sent=Stopwatch.GetTimestamp();
                var sentGcPauseTicks=GC.GetTotalPauseDuration().Ticks;
                var ping=new Ping(index+1,sent,sentGcPauseTicks);
                _pings.Enqueue(ping);
                // This is the only worker access to a UI service; no UI properties are read.
                var accepted=_dispatcher.TryEnqueue(()=>
                {
                    if (Volatile.Read(ref _stopped)!=0) { Interlocked.Exchange(ref ping.IgnoredAfterStop,1);return; }
                    var received=Stopwatch.GetTimestamp();
                    var receivedGcPauseTicks=GC.GetTotalPauseDuration().Ticks;
                    // Publish the timestamp last so a visible receipt includes its pause sample.
                    Interlocked.Exchange(ref ping.ReceivedGcPauseTicks,receivedGcPauseTicks);
                    Interlocked.Exchange(ref ping.Received,received);
                });
                Interlocked.Exchange(ref ping.Accepted,accepted ? 1 : -1);
                if (!accepted) break;
                await Task.Delay(IntervalMilliseconds,_cancellation.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch(Exception error) { Volatile.Write(ref _workerError,error.GetType().FullName+": "+error.Message); }
        finally { Interlocked.Exchange(ref _workerEnded,Stopwatch.GetTimestamp()); }
    }
    internal object StopAndSnapshot()
    {
        Dispose();
        var captured=Stopwatch.GetTimestamp();
        double? Milliseconds(long timestamp)=>timestamp==0 ? null : Stopwatch.GetElapsedTime(_started,timestamp).TotalMilliseconds;
        return new { StartedTimestamp=_started,CapturedTimestamp=captured,IntervalMilliseconds,MaximumPings,DeadlineMilliseconds,
            WorkerStartedMilliseconds=Milliseconds(Interlocked.Read(ref _workerStarted)),
            WorkerEndedMilliseconds=Milliseconds(Interlocked.Read(ref _workerEnded)),WorkerStatus=_worker.Status.ToString(),
            WorkerError=Volatile.Read(ref _workerError),CancellationRequested=true,
            Scope="Timestamp and cumulative process GC-pause pings within the original readiness wait. Clock/pause reads are sequential, not atomic. Pending callbacks are invalidated without awaiting or reading UI state.",
            Pings=_pings.ToArray().Select(ping=>
            {
                var received=Interlocked.Read(ref ping.Received);
                var receivedGcPauseTicks=received==0 ? (long?)null : Interlocked.Read(ref ping.ReceivedGcPauseTicks);
                var accepted=Volatile.Read(ref ping.Accepted);
                return new { ping.Sequence,SentTimestamp=ping.Sent,SentMilliseconds=Milliseconds(ping.Sent),
                    SentGcPauseMilliseconds=ping.SentGcPauseTicks/(double)TimeSpan.TicksPerMillisecond,
                    EnqueueAccepted=accepted==0 ? (bool?)null : accepted==1,
                    ReceivedTimestamp=received,ReceivedMilliseconds=Milliseconds(received),
                    ReceivedGcPauseMilliseconds=receivedGcPauseTicks/(double)TimeSpan.TicksPerMillisecond,
                    QueueGcPauseMilliseconds=(receivedGcPauseTicks-ping.SentGcPauseTicks)/(double)TimeSpan.TicksPerMillisecond,
                    QueueDelayMilliseconds=received==0 ? (double?)null : Stopwatch.GetElapsedTime(ping.Sent,received).TotalMilliseconds,
                    PendingAtSnapshot=accepted==1 && received==0 && Volatile.Read(ref ping.IgnoredAfterStop)==0,
                    IgnoredAfterStop=Volatile.Read(ref ping.IgnoredAfterStop)!=0 };
            }).ToArray() };
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped,1)!=0) return;
        _cancellation.Cancel();
        // Do not wait on the UI thread; dispose only once the worker has stopped using the token.
        _=_worker.ContinueWith(_=>_cancellation.Dispose(),CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    private sealed class Ping(int sequence,long sent,long sentGcPauseTicks)
    {
        internal readonly int Sequence=sequence;
        internal readonly long Sent=sent;
        internal readonly long SentGcPauseTicks=sentGcPauseTicks;
        internal long Received,ReceivedGcPauseTicks;
        internal int Accepted,IgnoredAfterStop;
    }
}
#endif
