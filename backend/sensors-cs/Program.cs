// sensors.exe — Streams hardware sensors as JSON lines.
//
// Cross-vendor ladder (first non-null wins, source name reported per tick):
//
//   1. LibreHardwareMonitor (`lhm`) — real-time MSR/SMU. Best when available.
//      Requires WinRing0 to load AND be allowed to touch MSRs. On Win 11 with
//      Memory Integrity OR Microsoft Vulnerable Driver Blocklist on, the
//      driver often loads but reads silently return 0, so we treat all-zero
//      CPU temp/power as "not available" and fall through.
//
//   2. Vendor WMI:
//        - Lenovo Legion (`lenovo`) — LENOVO_GAMEZONE_DATA.GetCPUTemp(). The
//          same source Lenovo Vantage uses; always live, no ring0 needed.
//        - ASUS / Dell / HP / MSI — startup enumeration only for now. The log
//          tells us which classes exist on a given machine; pollers can be
//          added the same way as Lenovo's.
//
//   3. Performance Counter thermal zone (`perfcounter`) —
//      Win32_PerfFormattedData_Counters_ThermalZoneInformation. Same
//      underlying _TMP source as ACPI but routed through the OS thermal
//      manager, which sometimes forces a fresher sample.
//
//   4. ACPI thermal zone (`acpi`) — MSAcpi_ThermalZoneTemperature. Universal
//      but EC-cached on many laptops; can be stale until OEM software pokes
//      the EC. We pick the highest plausible zone.
//
//   5. SMBIOS probe (`smbios`) — Win32_TemperatureProbe. Rarely populated on
//      consumer hardware but free to try.
//
// Each tier is independent and has its own availability flag, so a non-Lenovo
// machine simply skips Lenovo and proceeds to perfcounter/acpi without noise.
//
// Spawned by monitor.py. Requires admin (LHM driver + WMI thermal zones).
// Output: NDJSON one line per second.

using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Principal;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

string? logPath = null;
StreamWriter? logger = null;
try
{
    var dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PC Monitor");
    Directory.CreateDirectory(dir);
    logPath = Path.Combine(dir, "sensors-debug.log");
    logger = new StreamWriter(logPath, append: false) { AutoFlush = true };
}
catch { }

void Log(string s) { try { logger?.WriteLine(s); } catch { } }

bool isElevated;
try
{
    using var id = WindowsIdentity.GetCurrent();
    isElevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}
catch { isElevated = false; }

Log($"[{DateTime.Now:O}] sensors.exe starting");
Log($"  Elevated: {isElevated}, PID: {Environment.ProcessId}, Path: {Environment.ProcessPath}");

// --- Platform identification ---
string motherboardVendor = "?", motherboardProduct = "?", systemFamily = "?", cpuName = "?";
try
{
    using var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard");
    foreach (ManagementObject mo in s.Get())
    {
        motherboardVendor = (mo["Manufacturer"] as string ?? "?").Trim();
        motherboardProduct = (mo["Product"] as string ?? "?").Trim();
        break;
    }
}
catch { }
try
{
    using var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT SystemFamily FROM Win32_ComputerSystem");
    foreach (ManagementObject mo in s.Get())
    {
        systemFamily = (mo["SystemFamily"] as string ?? "?").Trim();
        break;
    }
}
catch { }
try
{
    using var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name FROM Win32_Processor");
    foreach (ManagementObject mo in s.Get())
    {
        cpuName = (mo["Name"] as string ?? "?").Trim();
        break;
    }
}
catch { }
Log($"  Motherboard: {motherboardVendor} / {motherboardProduct} ({systemFamily})");
Log($"  CPU: {cpuName}");

// --- LHM init ---
var computer = new Computer
{
    IsCpuEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
};
bool lhmOk = false;
try
{
    computer.Open();
    lhmOk = true;
    Log("LHM Computer.Open() succeeded");
}
catch (Exception ex)
{
    Log($"LHM Computer.Open() FAILED: {ex.Message}");
}

// --- WMI fallback: probe what's available once at startup ---
Log("");
Log("=== ACPI thermal zones (root/wmi MSAcpi_ThermalZoneTemperature) ===");
try
{
    using var searcher = new ManagementObjectSearcher(
        @"root\wmi", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
    int count = 0;
    foreach (ManagementObject mo in searcher.Get())
    {
        count++;
        var instance = mo["InstanceName"] as string ?? "?";
        var raw = mo["CurrentTemperature"];
        double? tempC = null;
        if (raw != null)
        {
            // CurrentTemperature is in tenths of Kelvin
            var k = Convert.ToDouble(raw, CultureInfo.InvariantCulture) / 10.0;
            tempC = Math.Round(k - 273.15, 1);
        }
        Log($"  TZ \"{instance}\" = {tempC?.ToString("F1") ?? "null"} C");
    }
    if (count == 0) Log("  (no thermal zones returned)");
}
catch (Exception ex)
{
    Log($"  WMI MSAcpi_ThermalZoneTemperature FAILED: {ex.Message}");
}

// Enumerate vendor WMI classes in root\WMI matching any of the given prefixes.
// Logs class names, instance properties, and thermal-relevant method names so
// future per-vendor pollers can be written from the log alone.
void ProbeVendorWmi(string vendorLabel, string[] prefixes)
{
    Log("");
    Log($"=== {vendorLabel} WMI namespace (root\\WMI {string.Join(", ", prefixes.Select(p => p + "*"))}) ===");
    try
    {
        using var classSearcher = new ManagementObjectSearcher(
            @"root\WMI", "SELECT * FROM meta_class");
        var matched = new List<string>();
        foreach (ManagementClass mc in classSearcher.Get())
        {
            var name = mc["__CLASS"] as string ?? "";
            foreach (var prefix in prefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(name);
                    break;
                }
            }
        }
        if (matched.Count == 0) { Log($"  (no {vendorLabel} WMI classes)"); return; }
        foreach (var cls in matched) Log($"  class: {cls}");

        foreach (var cls in matched.Where(c =>
            c.Contains("GameZone", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("Thermal", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("Temp", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("WMNB", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("ATK", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("ASUSWMI", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("Method", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var instSearcher = new ManagementObjectSearcher(
                    @"root\WMI", $"SELECT * FROM {cls}");
                int n = 0;
                foreach (ManagementObject mo in instSearcher.Get())
                {
                    n++;
                    Log($"    {cls}[{n}] properties:");
                    foreach (PropertyData p in mo.Properties)
                    {
                        object? val = null;
                        try { val = p.Value; } catch { val = "<err>"; }
                        Log($"      {p.Name} = {val}");
                    }
                    using var mc = new ManagementClass(@"root\WMI", cls, null);
                    foreach (MethodData m in mc.Methods)
                    {
                        if (m.Name.Contains("Temp", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains("Sensor", StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"      method: {m.Name}");
                        }
                    }
                    if (n >= 1) break;
                }
            }
            catch (Exception ex) { Log($"    {cls} probe failed: {ex.Message}"); }
        }
    }
    catch (Exception ex)
    {
        Log($"  {vendorLabel} WMI enumeration FAILED: {ex.Message}");
    }
}

ProbeVendorWmi("Lenovo", new[] { "Lenovo", "LENOVO_" });
ProbeVendorWmi("ASUS",   new[] { "ASUS", "Asus", "AsusAtkWmi", "ATK" });
ProbeVendorWmi("Dell",   new[] { "Dell", "DELL_" });
ProbeVendorWmi("HP",     new[] { "HP_", "HPQ" });
ProbeVendorWmi("MSI",    new[] { "MSI_", "MSI" });

Log("");
Log("=== SMBIOS temperature probes (Win32_TemperatureProbe) ===");
try
{
    using var searcher = new ManagementObjectSearcher(
        @"root\cimv2", "SELECT * FROM Win32_TemperatureProbe");
    int count = 0;
    foreach (ManagementObject mo in searcher.Get())
    {
        count++;
        Log($"  Probe \"{mo["Name"]}\" CurrentReading={mo["CurrentReading"]} Status={mo["Status"]}");
    }
    if (count == 0) Log("  (no temperature probes returned)");
}
catch (Exception ex)
{
    Log($"  WMI Win32_TemperatureProbe FAILED: {ex.Message}");
}

// --- LHM hardware enumeration (only meaningful if lhmOk) ---
if (lhmOk)
{
    foreach (var hw in computer.Hardware)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.Update();
    }
    Log("");
    Log("=== LHM hardware enumeration ===");
    foreach (var hw in computer.Hardware)
    {
        Log($"HW: {hw.HardwareType} - \"{hw.Name}\"");
        foreach (var s in hw.Sensors)
        {
            var v = s.Value.HasValue ? s.Value.Value.ToString("F2") : "null";
            Log($"  S: {s.SensorType,-12} \"{s.Name}\" = {v}");
        }
    }
}
Log("=== End enumeration ===");
Log("");

// --- Video adapter inventory (one-shot at startup) ---
// We list non-software GPUs known to Windows via Win32_VideoController. Used
// later to map WDDM perf-counter LUIDs to friendly names. Software adapters
// (Microsoft Basic Display) are skipped.
var videoAdapters = new List<(string Name, long AdapterRam)>();
try
{
    using var s = new ManagementObjectSearcher(@"root\cimv2",
        "SELECT Name, AdapterRAM, Status FROM Win32_VideoController");
    foreach (ManagementObject mo in s.Get())
    {
        var name = (mo["Name"] as string ?? "").Trim();
        var status = (mo["Status"] as string ?? "").Trim();
        if (name.Length == 0) continue;
        if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
        if (status.Length > 0 && !status.Equals("OK", StringComparison.OrdinalIgnoreCase)) continue;
        long ram = 0;
        try { ram = Convert.ToInt64(mo["AdapterRAM"] ?? 0); } catch { }
        videoAdapters.Add((name, ram));
    }
}
catch (Exception ex) { Log($"  Win32_VideoController enum failed: {ex.Message}"); }
Log("");
Log("=== Video adapters (Win32_VideoController) ===");
if (videoAdapters.Count == 0) Log("  (none)");
foreach (var a in videoAdapters)
    Log($"  {a.Name}  AdapterRAM={a.AdapterRam / 1024 / 1024} MB");

// --- WDDM GPU poller (cross-vendor: NVIDIA, AMD, Intel; integrated or discrete) ---
// Reads the same Windows performance counters Task Manager uses. No driver,
// no admin elevation needed for the read. Aggregates per-adapter (LUID).
//
// Instance name shape:
//   GPU Engine:          pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D
//   GPU Adapter Memory:  luid_0x00000000_0x0000ABCD_phys_0
//
// The LUID prefix (`luid_<hi>_<lo>_phys_<n>`) identifies the adapter; we
// aggregate engine utilisation across all engines belonging to one adapter.
var engineCounters = new Dictionary<string, PerformanceCounter>();
var memDedicatedCounters = new Dictionary<string, PerformanceCounter>();
var memSharedCounters = new Dictionary<string, PerformanceCounter>();
bool wddmAvailable = true;
int wddmTickCount = 0;

static string? ExtractLuidKey(string instance)
{
    // Returns "luid_<hi>_<lo>_phys_<n>" or null.
    int idx = instance.IndexOf("luid_", StringComparison.Ordinal);
    if (idx < 0) return null;
    int physIdx = instance.IndexOf("_phys_", idx, StringComparison.Ordinal);
    if (physIdx < 0) return instance.Substring(idx);
    int physEnd = instance.IndexOf('_', physIdx + "_phys_".Length);
    return physEnd > 0 ? instance.Substring(idx, physEnd - idx) : instance.Substring(idx);
}

(string? Name, double? UtilPct, long DedicatedBytes, long SharedBytes, long DedicatedTotalBytes)?
    PollWddmGpu()
{
    if (!wddmAvailable) return null;
    wddmTickCount++;
    try
    {
        var engineCat = new PerformanceCounterCategory("GPU Engine");
        var memCat = new PerformanceCounterCategory("GPU Adapter Memory");

        var utilByLuid = new Dictionary<string, double>();
        foreach (var inst in engineCat.GetInstanceNames())
        {
            var luid = ExtractLuidKey(inst);
            if (luid == null) continue;
            if (!engineCounters.TryGetValue(inst, out var counter))
            {
                try
                {
                    counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                    engineCounters[inst] = counter;
                }
                catch { continue; }
            }
            try
            {
                var v = counter.NextValue();
                if (!utilByLuid.ContainsKey(luid)) utilByLuid[luid] = 0;
                utilByLuid[luid] += v;
            }
            catch (InvalidOperationException) { engineCounters.Remove(inst); }
            catch { }
        }

        var dedicatedByLuid = new Dictionary<string, long>();
        var sharedByLuid = new Dictionary<string, long>();
        foreach (var inst in memCat.GetInstanceNames())
        {
            var luid = ExtractLuidKey(inst);
            if (luid == null) continue;

            if (!memDedicatedCounters.TryGetValue(inst, out var dc))
            {
                try
                {
                    dc = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, readOnly: true);
                    memDedicatedCounters[inst] = dc;
                }
                catch { dc = null; }
            }
            if (dc != null)
            {
                try { dedicatedByLuid[luid] = (long)dc.NextValue(); }
                catch (InvalidOperationException) { memDedicatedCounters.Remove(inst); }
                catch { }
            }

            if (!memSharedCounters.TryGetValue(inst, out var sc))
            {
                try
                {
                    sc = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", inst, readOnly: true);
                    memSharedCounters[inst] = sc;
                }
                catch { sc = null; }
            }
            if (sc != null)
            {
                try { sharedByLuid[luid] = (long)sc.NextValue(); }
                catch (InvalidOperationException) { memSharedCounters.Remove(inst); }
                catch { }
            }
        }

        if (utilByLuid.Count == 0 && dedicatedByLuid.Count == 0 && sharedByLuid.Count == 0)
            return null;

        // Pick primary adapter:
        //  - tick 1: util counters all read 0 by design, so prefer the adapter
        //    with the most dedicated VRAM (likely the dGPU on multi-GPU laptops).
        //  - tick 2+: pick the busiest adapter by current utilisation.
        string primaryLuid = wddmTickCount == 1 && dedicatedByLuid.Count > 0
            ? dedicatedByLuid.OrderByDescending(kv => kv.Value).First().Key
            : utilByLuid.Count > 0
                ? utilByLuid.OrderByDescending(kv => kv.Value).First().Key
                : (dedicatedByLuid.Keys.FirstOrDefault() ?? sharedByLuid.Keys.FirstOrDefault())!;

        // Win32_VideoController doesn't expose LUID directly. Heuristic name pick:
        // - 1 adapter total → use it.
        // - >1 adapter → pick the one with the most AdapterRAM (proxy for "primary").
        string? name = null;
        long totalBytes = 0;
        if (videoAdapters.Count == 1)
        {
            name = videoAdapters[0].Name;
            totalBytes = videoAdapters[0].AdapterRam;
        }
        else if (videoAdapters.Count > 1)
        {
            var primary = videoAdapters.OrderByDescending(a => a.AdapterRam).First();
            name = primary.Name;
            totalBytes = primary.AdapterRam;
        }

        double? util = null;
        if (wddmTickCount > 1 && utilByLuid.TryGetValue(primaryLuid, out var u))
            util = Math.Round(Math.Min(100.0, u), 1);

        return (
            Name: name,
            UtilPct: util,
            DedicatedBytes: dedicatedByLuid.GetValueOrDefault(primaryLuid),
            SharedBytes: sharedByLuid.GetValueOrDefault(primaryLuid),
            DedicatedTotalBytes: totalBytes
        );
    }
    catch (InvalidOperationException)
    {
        wddmAvailable = false;
        Log("  WDDM perf counters unavailable; disabling poller.");
        return null;
    }
    catch (Exception ex)
    {
        Log($"  WDDM poll failed: {ex.Message}");
        return null;
    }
}

// --- Lenovo Legion WMI poller ---
// LENOVO_GAMEZONE_DATA exposes GetCPUTemp() / GetGPUTemp() returning °C.
// Some BIOS versions return UInt32 °C, others return °C * 10. We sanity-check.
// Returns null on non-Lenovo systems or if the method/class is unavailable.
bool _lenovoWmiAvailable = true;
double? PollLenovoCpuTemp()
{
    if (!_lenovoWmiAvailable) return null;
    try
    {
        using var mc = new ManagementClass(@"root\WMI", "LENOVO_GAMEZONE_DATA", null);
        using var instances = mc.GetInstances();
        foreach (ManagementObject mo in instances)
        {
            using var result = mo.InvokeMethod("GetCPUTemp", null, null);
            if (result == null) continue;
            var raw = result["CurrentCPUTemperature"] ?? result["Data"] ?? result["ReturnValue"];
            if (raw == null) continue;
            var v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (v > 200) v /= 10.0;     // some BIOSes report deci-Celsius
            if (v < 20 || v > 120) continue;
            return Math.Round(v, 1);
        }
    }
    catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.InvalidClass ||
                                          mex.ErrorCode == ManagementStatus.NotFound)
    {
        _lenovoWmiAvailable = false;     // not a Lenovo Legion; stop trying
    }
    catch { /* transient — retry next tick */ }
    return null;
}

// --- WMI thermal zone polling helper (used in main loop) ---
double? PollAcpiThermalZone()
{
    try
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\wmi", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        double? best = null;
        foreach (ManagementObject mo in searcher.Get())
        {
            var raw = mo["CurrentTemperature"];
            if (raw == null) continue;
            var k = Convert.ToDouble(raw, CultureInfo.InvariantCulture) / 10.0;
            var c = k - 273.15;
            // Plausibility filter: reject placeholders like 27.85C (300K) and bogus negatives
            if (c < 20 || c > 120) continue;
            if (best == null || c > best) best = c;
        }
        return best.HasValue ? Math.Round(best.Value, 1) : null;
    }
    catch { return null; }
}

// --- Performance Counter thermal zone (Win32_PerfFormattedData_Counters_ThermalZoneInformation)
// Same _TMP source as ACPI but routed through the OS thermal manager. On some
// platforms this returns fresher samples than direct WMI.
bool _perfCounterAvailable = true;
double? PollPerfCounterThermalZone()
{
    if (!_perfCounterAvailable) return null;
    try
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT Name, HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
        double? best = null;
        int count = 0;
        foreach (ManagementObject mo in searcher.Get())
        {
            count++;
            // HighPrecisionTemperature: tenths of K. Temperature: whole K.
            double k;
            var hi = mo["HighPrecisionTemperature"];
            if (hi != null)
                k = Convert.ToDouble(hi, CultureInfo.InvariantCulture) / 10.0;
            else
            {
                var lo = mo["Temperature"];
                if (lo == null) continue;
                k = Convert.ToDouble(lo, CultureInfo.InvariantCulture);
            }
            var c = k - 273.15;
            if (c < 20 || c > 120) continue;
            if (best == null || c > best) best = c;
        }
        if (count == 0) _perfCounterAvailable = false;
        return best.HasValue ? Math.Round(best.Value, 1) : null;
    }
    catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.InvalidClass ||
                                          mex.ErrorCode == ManagementStatus.NotFound)
    {
        _perfCounterAvailable = false;
        return null;
    }
    catch { return null; }
}

// --- SMBIOS temperature probe (Win32_TemperatureProbe). Rarely populated on
// consumer hardware, but some workstations and servers do expose CPU/system
// temperatures here. CurrentReading is in tenths of Kelvin.
bool _smbiosAvailable = true;
double? PollSmbiosTemperatureProbe()
{
    if (!_smbiosAvailable) return null;
    try
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2", "SELECT CurrentReading FROM Win32_TemperatureProbe");
        double? best = null;
        int count = 0, populated = 0;
        foreach (ManagementObject mo in searcher.Get())
        {
            count++;
            var raw = mo["CurrentReading"];
            if (raw == null) continue;
            populated++;
            var k = Convert.ToDouble(raw, CultureInfo.InvariantCulture) / 10.0;
            var c = k - 273.15;
            if (c < 20 || c > 120) continue;
            if (best == null || c > best) best = c;
        }
        // If the class exists but no probe ever reports a value, stop polling.
        if (count == 0 || populated == 0) _smbiosAvailable = false;
        return best.HasValue ? Math.Round(best.Value, 1) : null;
    }
    catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.InvalidClass ||
                                          mex.ErrorCode == ManagementStatus.NotFound)
    {
        _smbiosAvailable = false;
        return null;
    }
    catch { return null; }
}

var ct = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { ct.Cancel(); e.Cancel = true; };

while (!ct.IsCancellationRequested)
{
    double? cpuTemp = null;
    double? cpuPower = null;
    int? fanRpm = null;
    string source = "none";

    if (lhmOk)
    {
        foreach (var hw in computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }

        foreach (var hw in computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.Value == null) continue;
                    var v = s.Value.Value;
                    var name = s.Name ?? "";

                    if (s.SensorType == SensorType.Temperature && v > 0)
                    {
                        if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("CCD", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            if (cpuTemp == null || v > cpuTemp) { cpuTemp = v; source = "lhm"; }
                        }
                    }
                    else if (s.SensorType == SensorType.Power && v > 0)
                    {
                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("CPU Cores", StringComparison.OrdinalIgnoreCase))
                        {
                            cpuPower ??= v;
                        }
                    }
                }
            }

            IEnumerable<IHardware> walk = new[] { hw }.Concat(hw.SubHardware);
            foreach (var h in walk)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.Value == null) continue;
                    if (s.SensorType == SensorType.Fan && s.Value.Value > 0)
                    {
                        var v = (int)s.Value.Value;
                        if (fanRpm == null || v > fanRpm) fanRpm = v;
                    }
                }
            }
        }
    }

    // Lenovo Legion WMI fallback (live; bypasses WinRing0 entirely)
    if (cpuTemp == null)
    {
        var lenovo = PollLenovoCpuTemp();
        if (lenovo.HasValue)
        {
            cpuTemp = lenovo.Value;
            source = "lenovo";
        }
    }

    // Performance Counter thermal zone (OS-managed; sometimes fresher than raw ACPI)
    if (cpuTemp == null)
    {
        var pc = PollPerfCounterThermalZone();
        if (pc.HasValue)
        {
            cpuTemp = pc.Value;
            source = "perfcounter";
        }
    }

    // ACPI thermal zone fallback (universal but EC-cached / often stale)
    if (cpuTemp == null)
    {
        var acpi = PollAcpiThermalZone();
        if (acpi.HasValue)
        {
            cpuTemp = acpi.Value;
            source = "acpi";
        }
    }

    // SMBIOS temperature probe (rarely populated on consumer hardware)
    if (cpuTemp == null)
    {
        var smb = PollSmbiosTemperatureProbe();
        if (smb.HasValue)
        {
            cpuTemp = smb.Value;
            source = "smbios";
        }
    }

    var wddm = PollWddmGpu();

    var payload = new
    {
        cpu_temperature = cpuTemp.HasValue ? Math.Round(cpuTemp.Value, 1) : (double?)null,
        cpu_power = cpuPower.HasValue ? Math.Round(cpuPower.Value, 1) : (double?)null,
        fan_rpm = fanRpm,
        source,
        // Static system identity (same every tick — the receiver caches once)
        system_cpu = cpuName,
        system_motherboard = motherboardProduct,
        // WDDM GPU snapshot (cross-vendor; null if perf counters unavailable)
        gpu_name = wddm?.Name,
        gpu_utilization = wddm?.UtilPct,
        gpu_memory_dedicated_mb = wddm.HasValue ? wddm.Value.DedicatedBytes / 1024 / 1024 : (long?)null,
        gpu_memory_shared_mb = wddm.HasValue ? wddm.Value.SharedBytes / 1024 / 1024 : (long?)null,
        gpu_memory_dedicated_total_mb = wddm.HasValue ? wddm.Value.DedicatedTotalBytes / 1024 / 1024 : (long?)null,
        gpu_source = wddm.HasValue ? "wddm" : null,
    };
    Console.WriteLine(JsonSerializer.Serialize(payload));
    Console.Out.Flush();

    try { await Task.Delay(1000, ct.Token); } catch (TaskCanceledException) { break; }
}

try { computer.Close(); } catch { }
try { logger?.Dispose(); } catch { }
return 0;
