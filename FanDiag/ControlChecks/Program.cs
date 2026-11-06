using FanController.Services;

// Isolated dust-controller checks: all driver calls use a fake transport.
var checks = new (string Name, Action Check)[]
{
    ("startup does not issue hardware requests", NoAutomaticStart),
    ("one click sends one start and stops at 11 seconds", SingleCycle),
    ("repeated clicks neither queue nor extend cleaning", RepeatedClicks),
    ("fresh click after completion starts one new cycle", StartAfterCompletion),
    ("queued old callbacks cannot stop newer cleaning", StaleCallbacks),
    ("manual stop restores normal without rearming", ManualStop),
    ("failed starts do not run", FailedStarts),
    ("failed restore is bounded and can be retried", RetryRestore),
    ("timer setup failure stops the accepted dust request", TimerFailure),
    ("subscriber callbacks do not hold the hardware lock", EventLock),
};
foreach (var check in checks)
{
    check.Check();
    Console.WriteLine("PASS " + check.Name);
}
Console.WriteLine($"{checks.Length} dust checks passed; no hardware driver was opened.");

static (EnergyDrvController Control, FakeDriver Driver, ManualClock Clock) Rig(bool automatic = false)
{
    var driver = new FakeDriver();
    var clock = new ManualClock();
    return (new EnergyDrvController(driver, clock, automatic), driver, clock);
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Normal(EnergyDrvController controller, FakeDriver driver)
{
    Require(!controller.IsRunning && controller.CurrentMode == FanControlMode.Normal, "Controller still running");
    Require(!controller.RestorePending && driver.Writes[^1] == 0, "Normal restoration was not acknowledged");
}
static void NoAutomaticStart()
{
    var (c, d, time) = Rig(automatic: true);
    time.Advance(60000); c.Tick();
    Require(!c.IsRunning && !c.RestorePending && d.DriverCalls == 0 && time.Timers.Count == 0,
        "Constructing an idle controller touched the driver or scheduled work");
}
static void SingleCycle()
{
    var (c, d, time) = Rig();
    Require(EnergyDrvFan.DustCycleDuration == TimeSpan.FromSeconds(11), "Unexpected cycle duration");
    Require(c.StartDustCleaning(), "Dust start failed");
    Require(c.CurrentMode == FanControlMode.DustCleaning, "Cleaning not active");
    time.Advance(9000); c.Tick();
    time.Advance(1999); c.Tick();
    Require(c.IsRunning && d.Writes.SequenceEqual(new[] { 0, 1 }), "Single cycle ended early or rearmed");
    time.Advance(1); c.Tick(); Normal(c, d);
    Require(d.Writes.SequenceEqual(new[] { 0, 1, 0 }) && d.Reads == 0, "Cycle did not stop once or polled unverified state");
    time.Advance(60000); c.Tick();
    Require(d.Writes.SequenceEqual(new[] { 0, 1, 0 }), "Completed cleaning restarted by itself");
}
static void RepeatedClicks()
{
    var (c, d, time) = Rig(automatic: true);
    Require(c.StartDustCleaning(), "Dust start failed");
    int before = d.DriverCalls;
    time.Advance(1000);
    Require(!c.StartDustCleaning(), "Repeated click was accepted as new cleaning");
    Require(d.DriverCalls == before && time.Timers.Count == 1 && c.LastMessage.Contains("未重复触发"),
        "Repeated click accessed hardware, queued a cycle or changed the timer");
    time.Advance(9999); c.Tick();
    Require(c.IsRunning, "Cleaning stopped before its original deadline");
    time.Advance(1); c.Tick(); Normal(c, d);
    Require(d.Writes.SequenceEqual(new[] { 0, 1, 0 }), "Repeated click extended or queued cleaning");
}
static void StartAfterCompletion()
{
    var (c, d, time) = Rig();
    c.StartDustCleaning(); time.Advance(11000); c.Tick();
    Require(c.StartDustCleaning(), "Fresh click after completion was rejected");
    Require(d.Writes.SequenceEqual(new[] { 0, 1, 0, 0, 1 }), "Fresh click did not send exactly one new start");
    time.Advance(10999); c.Tick(); Require(c.IsRunning, "New cycle inherited the old deadline");
    time.Advance(1); c.Tick(); Normal(c, d);
    Require(d.Writes.Count(mode => mode == 1) == 2, "Unexpected extra dust start");
}
static void StaleCallbacks()
{
    var (c, d, time) = Rig(automatic: true);
    c.StartDustCleaning();
    var oldTimer = time.Timers[^1];
    time.Advance(11000); oldTimer.FireQueuedCallback(); Normal(c, d);
    Require(oldTimer.Disposed, "Completed cycle left a live timer");
    c.StartDustCleaning();
    int before = d.DriverCalls;
    oldTimer.FireQueuedCallback();
    Require(c.IsRunning && d.DriverCalls == before, "Old callback changed the newer cycle");
    c.Stop(); before = d.DriverCalls;
    time.Timers[^1].FireQueuedCallback(); oldTimer.FireQueuedCallback();
    Require(d.DriverCalls == before, "A disposed callback wrote after manual stop");
}
static void ManualStop()
{
    var (c, d, time) = Rig(automatic: true);
    c.StartDustCleaning(); time.Advance(1000); c.Stop("程序退出"); Normal(c, d);
    Require(c.LastMessage.Contains("程序退出") && d.Writes.SequenceEqual(new[] { 0, 1, 0 }),
        "Manual stop did not restore once");
    time.Advance(60000); time.Timers[0].FireQueuedCallback();
    Require(d.Writes.SequenceEqual(new[] { 0, 1, 0 }), "Manual stop queued a restart");
}
static void FailedStarts()
{
    var (c, d, time) = Rig(automatic: true);
    d.IsAvailable = false;
    Require(!c.StartDustCleaning() && !c.IsRunning && d.Writes.Count == 0, "Unavailable driver accepted");
    d.IsAvailable = true;
    d.Fail = (mode, _) => mode == 1;
    Require(!c.StartDustCleaning(), "Failed dust write accepted");
    Normal(c, d);
    Require(d.Writes.SequenceEqual(new[] { 0, 1, 0 }) && time.Timers.Count == 0, "Failed start was not restored or left a timer");

    var (failedRestore, blockedDriver, _) = Rig();
    blockedDriver.Fail = (_, _) => true;
    Require(!failedRestore.StartDustCleaning() && !failedRestore.IsRunning && failedRestore.RestorePending,
        "Initial restoration failure was ignored");
    Require(blockedDriver.Writes.SequenceEqual(new[] { 0, 0, 0 }), "Dust started without successful initial restoration");
}
static void RetryRestore()
{
    var (c, d, time) = Rig();
    c.StartDustCleaning(); d.Fail = (mode, _) => mode == 0;
    time.Advance(11000); c.Tick();
    Require(!c.IsRunning && c.RestorePending && c.LastMessage.Contains("恢复请求失败"), "Failed recovery was reported as success");
    Require(d.Writes.SequenceEqual(new[] { 0, 1, 0, 0, 0 }), "Recovery retries were not bounded");
    int before = d.DriverCalls; time.Advance(60000); c.Tick();
    Require(d.DriverCalls == before, "Failed recovery continued issuing background requests");
    d.Fail = null; c.Stop(); Normal(c, d);
    Require(d.Writes.Count(mode => mode == 1) == 1, "Recovery retry restarted dust cleaning");
}
static void TimerFailure()
{
    var (c, d, time) = Rig(automatic: true);
    time.ThrowOnCreate = true;
    Require(!c.StartDustCleaning(), "Timer creation failure was ignored");
    Normal(c, d);
    Require(d.Writes.SequenceEqual(new[] { 0, 1, 0 }), "Accepted dust request was not stopped after timer setup failed");
}
static void EventLock()
{
    var (c, d, _) = Rig();
    bool observed = false;
    c.StateChanged += (_, _) => throw new InvalidOperationException("Fake subscriber failure");
    c.StateChanged += (_, _) => observed = Task.Run(() => c.ReadState()).Wait(1000);
    Require(c.StartDustCleaning(), "Subscriber failure prevented cleaning from starting");
    Require(observed, "Subscriber ran under hardware lock or was skipped after another subscriber threw");
    c.Stop(); Normal(c, d);
}

sealed class FakeDriver : IEnergyDrvTransport
{
    private bool _available = true;
    public int AvailabilityChecks { get; private set; }
    public int Reads { get; private set; }
    public int DriverCalls => AvailabilityChecks + Reads + Writes.Count;
    public bool IsAvailable
    {
        get { AvailabilityChecks++; return _available; }
        set => _available = value;
    }
    public List<int> Writes { get; } = new();
    public Func<int, int, bool>? Fail { get; set; }
    public bool SetMode(int mode)
    {
        Writes.Add(mode);
        return Fail?.Invoke(mode, Writes.Count) != true;
    }
    public int? ReadState() { Reads++; return 133; }
}
sealed class ManualClock : TimeProvider
{
    private long _ticks;
    public bool ThrowOnCreate { get; set; }
    public List<ManualTimer> Timers { get; } = new();
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(long milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (ThrowOnCreate) throw new InvalidOperationException("Fake timer setup failure");
        var timer = new ManualTimer(callback, state);
        Timers.Add(timer);
        return timer;
    }
}
sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
{
    public bool Disposed { get; private set; }
    public void FireQueuedCallback() => callback(state); // may be queued already when Dispose runs
    public bool Change(TimeSpan dueTime, TimeSpan period) => true;
    public void Dispose() => Disposed = true;
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
