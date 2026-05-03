// sensors.exe — Streams LibreHardwareMonitor CPU/motherboard sensors as JSON lines.
// Spawned by monitor.py as a child process. Requires admin (LHM loads the WinRing0
// kernel driver on Computer.Open()).
//
// Output format (one NDJSON line per second):
//   {"cpu_temperature": 51.3, "cpu_power": 14.2, "fan_rpm": 2850}
// All fields may be null if a sensor isn't present.

using System.Globalization;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var computer = new Computer
{
    IsCpuEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
};

try
{
    computer.Open();
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
    Console.Out.Flush();
    return 1;
}

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
return 0;
