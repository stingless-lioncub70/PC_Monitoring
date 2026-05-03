// sensors.exe — Streams LibreHardwareMonitor CPU/motherboard sensors as JSON lines.
// Spawned by monitor.py as a child process. Requires admin (LHM loads the WinRing0
// kernel driver on Computer.Open()).
//
// Output format (one NDJSON line per second):
//   {"cpu_temperature": 51.3, "cpu_power": 14.2, "fan_rpm": 2850}
// All fields may be null if a sensor isn't present.
//
// Diagnostic log: %LOCALAPPDATA%\PC Monitor\sensors-debug.log — written once at
// startup, lists every hardware/sensor LHM found and whether the process is
// elevated.

using System.Globalization;
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
catch
{
    // logging is best-effort; continue without it
}

void Log(string s)
{
    try { logger?.WriteLine(s); } catch { }
}

bool isElevated;
try
{
    using var id = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(id);
    isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
}
catch { isElevated = false; }

Log($"[{DateTime.Now:O}] sensors.exe starting");
Log($"  PID: {Environment.ProcessId}");
Log($"  User: {Environment.UserName}");
Log($"  Elevated: {isElevated}");
Log($"  CWD: {Environment.CurrentDirectory}");
Log($"  ExePath: {Environment.ProcessPath}");

var computer = new Computer
{
    IsCpuEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
};

try
{
    computer.Open();
    Log("Computer.Open() succeeded");
}
catch (Exception ex)
{
    Log($"Computer.Open() FAILED: {ex.Message}");
    Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
    Console.Out.Flush();
    return 1;
}

// First-pass enumeration so we can see what LHM detected
foreach (var hw in computer.Hardware)
{
    hw.Update();
    foreach (var sub in hw.SubHardware) sub.Update();
}
Log("");
Log("=== Hardware enumeration (initial Update) ===");
foreach (var hw in computer.Hardware)
{
    Log($"HW: {hw.HardwareType} - \"{hw.Name}\"");
    foreach (var s in hw.Sensors)
    {
        var v = s.Value.HasValue ? s.Value.Value.ToString("F2") : "null";
        Log($"  S: {s.SensorType,-12} \"{s.Name}\" = {v}");
    }
    foreach (var sub in hw.SubHardware)
    {
        Log($"  SubHW: {sub.HardwareType} - \"{sub.Name}\"");
        foreach (var s in sub.Sensors)
        {
            var v = s.Value.HasValue ? s.Value.Value.ToString("F2") : "null";
            Log($"    S: {s.SensorType,-12} \"{s.Name}\" = {v}");
        }
    }
}
Log("=== End enumeration ===");
Log("");

var ct = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { ct.Cancel(); e.Cancel = true; };

while (!ct.IsCancellationRequested)
{
    foreach (var hw in computer.Hardware)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.Update();
    }

    double? cpuTemp = null;
    double? cpuPower = null;
    int? fanRpm = null;

    foreach (var hw in computer.Hardware)
    {
        if (hw.HardwareType == HardwareType.Cpu)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.Value == null) continue;
                var name = s.Name ?? string.Empty;

                if (s.SensorType == SensorType.Temperature)
                {
                    if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("CCD", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = s.Value.Value;
                        if (cpuTemp == null || v > cpuTemp) cpuTemp = v;
                    }
                }
                else if (s.SensorType == SensorType.Power)
                {
                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("CPU Cores", StringComparison.OrdinalIgnoreCase))
                    {
                        cpuPower ??= s.Value.Value;
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
                if (s.SensorType == SensorType.Fan)
                {
                    var v = s.Value.Value;
                    if (v > 0 && (fanRpm == null || v > fanRpm)) fanRpm = (int)v;
                }
            }
        }
    }

    var payload = new
    {
        cpu_temperature = cpuTemp.HasValue ? Math.Round(cpuTemp.Value, 1) : (double?)null,
        cpu_power = cpuPower.HasValue ? Math.Round(cpuPower.Value, 1) : (double?)null,
        fan_rpm = fanRpm,
    };
    Console.WriteLine(JsonSerializer.Serialize(payload));
    Console.Out.Flush();

    try { await Task.Delay(1000, ct.Token); } catch (TaskCanceledException) { break; }
}

try { computer.Close(); } catch { }
try { logger?.Dispose(); } catch { }
return 0;
