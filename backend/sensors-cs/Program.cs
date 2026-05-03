// sensors.exe — Streams hardware sensors as JSON lines.
//
// Strategy:
//   1. Try LibreHardwareMonitor (real values, needs WinRing0 driver).
//      On Win 11 with WDAC enforcement, the driver may be blocked, in which
//      case LHM returns 0 / null for MSR-based sensors.
//   2. Fall back to WMI:
//        - MSAcpi_ThermalZoneTemperature (root\wmi) — ACPI thermal zones
//        - Win32_TemperatureProbe (root\cimv2) — SMBIOS temperature probes
//   3. Whichever produces a sensible nonzero CPU temp wins.
//
// Spawned by monitor.py. Requires admin (LHM driver + WMI thermal zones).
// Output: NDJSON one line per second.

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

    // ACPI thermal zone fallback when LHM produced no usable temp
    if (cpuTemp == null)
    {
        var acpi = PollAcpiThermalZone();
        if (acpi.HasValue)
        {
            cpuTemp = acpi.Value;
            source = "acpi";
        }
    }

    var payload = new
    {
        cpu_temperature = cpuTemp.HasValue ? Math.Round(cpuTemp.Value, 1) : (double?)null,
        cpu_power = cpuPower.HasValue ? Math.Round(cpuPower.Value, 1) : (double?)null,
        fan_rpm = fanRpm,
        source,
    };
    Console.WriteLine(JsonSerializer.Serialize(payload));
    Console.Out.Flush();

    try { await Task.Delay(1000, ct.Token); } catch (TaskCanceledException) { break; }
}

try { computer.Close(); } catch { }
try { logger?.Dispose(); } catch { }
return 0;
