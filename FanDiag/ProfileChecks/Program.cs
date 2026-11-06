using FanController.Services;

// Fake transport: no WMI calls, device opens, or real firmware writes.
var checks = new (string Name, Action Run)[]
{
    ("constructor has no I/O", Construction),
    ("unsupported machine never opens driver", UnsupportedMachine),
    ("read uses only capability zero and mode seven", ReadOnlyRefresh),
    ("unsupported capabilities reject setters", Capabilities),
    ("known mode values decode without guessing", ModeDecode),
    ("all setters use exact commands and read back", SetCommands),
    ("invalid selection never sends firmware requests", InvalidSelection),
    ("driver errors never claim successful settings", DriverFailures),
    ("firmware errors never claim successful settings", FirmwareFailures),
    ("failed or mismatched readback clears current mode", ReadbackFailures),
    ("external Fn+Q changes are picked up on refresh", ExternalChange),
    ("short and failed native replies are rejected", NativeReplyLength),
};
foreach (var check in checks) { check.Run(); Console.WriteLine("PASS " + check.Name); }
Console.WriteLine($"{checks.Length} profile checks passed; no hardware driver was opened.");

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static (ThermalProfileService Service, FakeTransport Driver) Rig()
{
    var driver = new FakeTransport();
    return (new ThermalProfileService(driver, () => (true, "81YN / DTCN27WW(V1.13)")), driver);
}
static void Construction()
{
    var driver = new FakeTransport();
    bool queried = false;
    var service = new ThermalProfileService(driver, () => { queried = true; return (true, "test"); });
    Require(!queried && driver.Commands.Count == 0 && !service.IsSupported && service.CurrentProfile == ThermalProfile.Unknown,
        "Construction performed I/O or claimed a mode");
}
static void UnsupportedMachine()
{
    var driver = new FakeTransport();
    var service = new ThermalProfileService(driver, () => (false, "another machine"));
    Require(!service.Refresh() && !service.SetProfile(ThermalProfile.ExtremePerformance), "Unsupported machine accepted");
    Require(driver.Commands.Count == 0, "Unsupported machine reached driver");
}
static void ReadOnlyRefresh()
{
    var (service, driver) = Rig();
    Require(service.Refresh() && service.IsSupported, "Supported interface rejected");
    Require(driver.Commands.SequenceEqual(new uint[] { 0, 7 }), "Refresh did not use the read-only command sequence");
    Require(service.CurrentProfile == ThermalProfile.IntelligentCooling, "Current mode incorrect");
    Require(service.Refresh() && driver.Commands.SequenceEqual(new uint[] { 0, 7, 7 }), "Refresh repeated capability or used GET2");
}
static void Capabilities()
{
    foreach (uint cap in new uint[] { 0, 4, 0x50000001, 0x40000101, 0x60000101 })
    {
        var (service, driver) = Rig(); driver.Capability = cap;
        Require(!service.SetProfile(ThermalProfile.ExtremePerformance) && !service.IsSupported, "Unverified capability accepted");
        Require(driver.Commands.SequenceEqual(new uint[] { 0 }), "Setting sent before support was verified");
    }
}
static void ModeDecode()
{
    foreach (var mode in new[] { ThermalProfile.IntelligentCooling, ThermalProfile.ExtremePerformance, ThermalProfile.BatterySaving })
    {
        var (service, driver) = Rig(); driver.Mode = mode;
        Require(service.Refresh() && service.CurrentProfile == mode, "Known mode not decoded");
    }
    foreach (uint raw in new uint[] { 0, 4, 0x00010001, 0x00020101, 0x00FF0001 })
    {
        var (service, driver) = Rig(); driver.QueryReply = raw;
        Require(!service.Refresh() && service.CurrentProfile == ThermalProfile.Unknown, "Unknown/failed reply guessed as a valid mode");
    }
}
static void SetCommands()
{
    var expected = new[] { (ThermalProfile.IntelligentCooling, 0x000FB001u),
        (ThermalProfile.ExtremePerformance, 0x0012B001u), (ThermalProfile.BatterySaving, 0x0013B001u) };
    foreach (var (mode, command) in expected)
    {
        var (service, driver) = Rig();
        Require(service.SetProfile(mode) && service.CurrentProfile == mode, "Set mode not verified");
        Require(driver.Commands.SequenceEqual(new[] { 0u, command, 7u }), "Wrong wire command or missing readback");
    }
}
static void InvalidSelection()
{
    var (service, driver) = Rig();
    Require(!service.SetProfile(ThermalProfile.Unknown) && !service.SetProfile((ThermalProfile)1), "Unknown mode accepted");
    Require(driver.Commands.Count == 0, "Invalid selection reached firmware");
}
static void DriverFailures()
{
    foreach (uint failure in new uint[] { 0, 7, 0x0012B001 })
    {
        var (service, driver) = Rig(); driver.FailCommand = failure;
        Require(!service.SetProfile(ThermalProfile.ExtremePerformance) && service.CurrentProfile == ThermalProfile.Unknown,
            "Driver failure was presented as success");
    }
    var (other, throwing) = Rig(); throwing.Throw = true;
    Require(!other.Refresh() && other.CurrentProfile == ThermalProfile.Unknown, "Exception escaped query boundary");
}
static void FirmwareFailures()
{
    var (service, driver) = Rig(); driver.SetReply = 4;
    Require(!service.SetProfile(ThermalProfile.BatterySaving) && service.CurrentProfile == ThermalProfile.Unknown,
        "Firmware error accepted");
    Require(driver.Commands.SequenceEqual(new uint[] { 0, 0x0013B001 }), "Readback continued after rejected setting");
}
static void ReadbackFailures()
{
    var (service, driver) = Rig(); Require(service.Refresh(), "Initial query failed");
    driver.FreezeMode = true;
    Require(!service.SetProfile(ThermalProfile.ExtremePerformance) && service.CurrentProfile == ThermalProfile.Unknown,
        "Mismatched readback claimed success");
    driver.FailCommand = 7;
    Require(!service.SetProfile(ThermalProfile.BatterySaving) && service.CurrentProfile == ThermalProfile.Unknown,
        "Failed readback retained old/current requested mode");
}
static void ExternalChange()
{
    var driver = new FakeTransport(); int identities = 0;
    var service = new ThermalProfileService(driver, () => { identities++; return (true, "test"); });
    Require(service.Refresh(), "First refresh failed");
    driver.Mode = ThermalProfile.BatterySaving;
    Require(service.Refresh() && service.CurrentProfile == ThermalProfile.BatterySaving && identities == 1,
        "External Fn+Q change was not read or identity was repeatedly queried");
    Require(driver.Commands.All(c => c is 0 or 7), "Polling sent a setting command");
}
static void NativeReplyLength()
{
    foreach (uint length in new uint[] { 0, 1, 2, 3, 5, 8 })
        Require(!NativeThermalProfileTransport.ValidateResponse(true, length, 0x00020001, 0, out uint reply, out _)
            && reply == 0, "Malformed driver response accepted");
    Require(!NativeThermalProfileTransport.ValidateResponse(false, 4, 0x00020001, 5, out uint failed, out _)
        && failed == 0, "Failed IOCTL accepted");
    Require(NativeThermalProfileTransport.ValidateResponse(true, 4, 0x00020001, 0, out uint valid, out _)
        && valid == 0x00020001, "Valid four-byte response rejected");
}

sealed class FakeTransport : IThermalProfileTransport
{
    public List<uint> Commands { get; } = new();
    public uint Capability { get; set; } = 0x50000101;
    public ThermalProfile Mode { get; set; } = ThermalProfile.IntelligentCooling;
    public uint? QueryReply { get; set; }
    public uint SetReply { get; set; } = 1;
    public uint? FailCommand { get; set; }
    public bool FreezeMode { get; set; }
    public bool Throw { get; set; }
    public bool TryCall(uint command, out uint reply, out string error)
    {
        Commands.Add(command); reply = 0; error = "fake failure";
        if (Throw) throw new IOException("fake unavailable driver");
        if (FailCommand == command) return false;
        error = "";
        if (command == 0) { reply = Capability; return true; }
        if (command == 7) { reply = QueryReply ?? (((uint)Mode << 16) | 1); return true; }
        if (command is 0x000FB001 or 0x0012B001 or 0x0013B001)
        {
            reply = SetReply;
            if (!FreezeMode && reply == 1) Mode = command switch
            {
                0x000FB001 => ThermalProfile.IntelligentCooling,
                0x0012B001 => ThermalProfile.ExtremePerformance,
                _ => ThermalProfile.BatterySaving,
            };
            return true;
        }
        throw new InvalidOperationException($"Unexpected firmware command 0x{command:X8}");
    }
}
