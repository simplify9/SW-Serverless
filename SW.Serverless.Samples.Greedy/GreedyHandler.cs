using SW.Serverless.Sdk.Resident;

namespace SW.Serverless.Samples.Greedy;

/// <summary>
/// An adapter that misbehaves on request, so the host's resource ceilings have something real to
/// act on.
///
/// The limits are enforced against a live operating-system process — resident set size and
/// processor time — so a fake cannot exercise them. This allocates for real and burns a core for
/// real, which is the only way to find out whether the watchdog samples, decides and acts the way
/// it claims to.
///
/// It also reports honestly while doing it, because half of what the ceilings are for is being
/// visible before they fire.
/// </summary>
public class GreedyHandler : IResidentAdapter
{
    private IAdapterContext _context;
    private CancellationTokenSource _stopping;

    // Held in a field, not a local: the whole point is that the GC cannot reclaim it, or RSS never
    // moves and the soft ceiling never trips.
    private readonly List<byte[]> _held = new();
    private readonly object _gate = new();

    private volatile string _state = "Idle";
    private long _allocatedMb;
    private Task _burn;

    public Task StartAsync(IAdapterContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _state = "Ready";

        // Allocate at startup when asked, so a test can watch a ceiling fire without a round trip
        // — the adapter is over the line before the first heartbeat samples it.
        if (int.TryParse(context.StartupValueOf("AllocateMbOnStart"), out var mb) && mb > 0)
            AllocateCore(mb);

        if (double.TryParse(context.StartupValueOf("BurnCpuOnStart"), out var seconds) && seconds > 0)
            StartBurn(seconds);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _state = "Stopped";
        _stopping?.Cancel();
        lock (_gate) _held.Clear();
        return Task.CompletedTask;
    }

    public Task<AdapterStatus> GetStatusAsync()
    {
        var status = new AdapterStatus
        {
            Connected = true,
            State = _state,
        };

        status.Details["allocatedMb"] = Interlocked.Read(ref _allocatedMb).ToString();
        status.Details["workingSetMb"] =
            (Environment.WorkingSet / 1024 / 1024).ToString();
        status.Details["burning"] = (_burn is { IsCompleted: false }).ToString();

        return Task.FromResult(status);
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Holds another <paramref name="megabytes"/> so the process's RSS actually grows.</summary>
    public Task<object> Allocate(int megabytes)
    {
        AllocateCore(megabytes);
        return Task.FromResult<object>(new { allocatedMb = Interlocked.Read(ref _allocatedMb) });
    }

    private void AllocateCore(int megabytes)
    {
        _state = "Allocating";

        for (var i = 0; i < megabytes; i++)
        {
            var block = new byte[1024 * 1024];

            // Touch every page. A .NET array is committed lazily enough that an untouched
            // allocation may never appear in the resident set — and RSS is what the host samples.
            for (var b = 0; b < block.Length; b += 4096) block[b] = 1;

            lock (_gate) _held.Add(block);
            Interlocked.Increment(ref _allocatedMb);
        }

        _state = "Allocated";
        _context?.LogInformation($"Holding {Interlocked.Read(ref _allocatedMb)} MB.");
    }

    /// <summary>Lets go of everything, so a test can prove a ceiling stops tripping.</summary>
    public Task<object> Release()
    {
        lock (_gate) _held.Clear();
        Interlocked.Exchange(ref _allocatedMb, 0);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _state = "Ready";
        return Task.FromResult<object>(new { allocatedMb = 0 });
    }

    /// <summary>Pegs a core for a while, so the sustained CPU ceiling has something to sample.</summary>
    public Task<object> BurnCpu(double seconds)
    {
        StartBurn(seconds);
        return Task.FromResult<object>(new { burningFor = seconds });
    }

    private void StartBurn(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        _state = "Burning";

        _burn = Task.Run(() =>
        {
            // A tight loop on purpose: the ceiling is about sustained processor time, and anything
            // that yields would not produce it.
            var spin = 0UL;
            while (DateTime.UtcNow < until && !(_stopping?.IsCancellationRequested ?? false))
                for (var i = 0; i < 5_000_000; i++) spin += (ulong)i;

            _state = "Ready";
            _context?.LogInformation($"Burn finished ({spin & 1}).");
        });
    }

    public Task<object> GetStats() => Task.FromResult<object>(new
    {
        allocatedMb = Interlocked.Read(ref _allocatedMb),
        workingSetMb = Environment.WorkingSet / 1024 / 1024,
        burning = _burn is { IsCompleted: false },
        state = _state,
    });
}
