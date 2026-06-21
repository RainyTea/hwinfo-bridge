using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace HwInfoBridge;

internal sealed class SensorMap
{
    public string? CpuUsage { get; set; }
    public string? CpuTemp { get; set; }
    public string? CpuWatts { get; set; }
    public string? CpuCoreClock { get; set; }
    public string? GpuUsage { get; set; }
    public string? GpuTemp { get; set; }
    public string? GpuWatts { get; set; }
    public string? GpuCoreClock { get; set; }
    public string? GpuMemoryAvailable { get; set; }
    public string? GpuMemoryAllocated { get; set; }
}

internal sealed class Config
{
    public int Port { get; set; } = 8765;
    public int PollMs { get; set; } = 1000;
    public SensorMap Sensors { get; set; } = new();
}

internal sealed class CpuPayload
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("usage")] public double? Usage { get; set; }
    [JsonPropertyName("temp")] public double? Temp { get; set; }
    [JsonPropertyName("watts")] public double? Watts { get; set; }
    [JsonPropertyName("coreClockMhz")] public double? CoreClockMhz { get; set; }
}

internal sealed class GpuPayload
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("usage")] public double? Usage { get; set; }
    [JsonPropertyName("temp")] public double? Temp { get; set; }
    [JsonPropertyName("watts")] public double? Watts { get; set; }
    [JsonPropertyName("coreClockMhz")] public double? CoreClockMhz { get; set; }
    [JsonPropertyName("memory")] public MemoryPayload Memory { get; set; } = new();
}

internal sealed class MemoryPayload
{
    [JsonPropertyName("totalGb")] public double? TotalGb { get; set; }
    [JsonPropertyName("usedGb")] public double? UsedGb { get; set; }
}

internal sealed class StoragePayload
{
    [JsonPropertyName("totalGb")] public double? TotalGb { get; set; }
}

internal sealed class SystemPayload
{
    [JsonPropertyName("watts")] public double? Watts { get; set; }
}

internal sealed class SensorPayload
{
    [JsonPropertyName("cpu")] public CpuPayload Cpu { get; set; } = new();
    [JsonPropertyName("gpu")] public GpuPayload Gpu { get; set; } = new();
    [JsonPropertyName("memory")] public MemoryPayload Memory { get; set; } = new();
    [JsonPropertyName("storage")] public StoragePayload Storage { get; set; } = new();
    [JsonPropertyName("system")] public SystemPayload System { get; set; } = new();
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SensorPayload))]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(Dictionary<string, double>))]
internal partial class AppJsonContext : JsonSerializerContext { }

internal static class Program
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "HwInfoBridge";
    private const string VsbKey = @"Software\HWiNFO64\VSB";
    private const string GpuClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static volatile string _sensorsJson = "{}";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKb);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return args[0].ToLowerInvariant() switch
            {
                "--install" => InstallStartup(),
                "--uninstall" => UninstallStartup(),
                _ => 2,
            };
        }

        var config = LoadConfig();
        new Thread(() => PollLoop(config)) { IsBackground = true, Name = "hwinfo-poll" }.Start();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{config.Port}/");
        try { listener.Start(); } catch { return 1; }

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); } catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
        }
        return 0;
    }

    private static int InstallStartup()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return 1;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RunValueName, $"\"{exe}\"");
        return 0;
    }

    private static int UninstallStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        return 0;
    }

    private static void Handle(HttpListenerContext ctx)
    {
        try
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Cache-Control"] = "no-store";

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                ctx.Response.StatusCode = 204;
                return;
            }

            var body = ctx.Request.Url?.AbsolutePath switch
            {
                "/sensors" => _sensorsJson,
                "/labels" => JsonSerializer.Serialize(
                    ReadHwInfoLabels(), AppJsonContext.Default.DictionaryStringDouble),
                _ => null,
            };

            if (body is null) { ctx.Response.StatusCode = 404; return; }

            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch { }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private static void PollLoop(Config config)
    {
        var cpuName = ReadCpuName();
        var gpuName = ReadGpuName();
        var storageGb = TryGetTotalFixedStorageGb();
        var s = config.Sensors;

        while (true)
        {
            try
            {
                var labels = ReadHwInfoLabels();
                var (memTotalGb, memUsedGb) = ReadMemoryStatus();
                var cpuW = Match(labels, s.CpuWatts);
                var gpuW = Match(labels, s.GpuWatts);
                var gpuMemAllocMb = Match(labels, s.GpuMemoryAllocated);
                var gpuMemAvailMb = Match(labels, s.GpuMemoryAvailable);

                _sensorsJson = JsonSerializer.Serialize(new SensorPayload
                {
                    Cpu = new CpuPayload
                    {
                        Name = cpuName,
                        Usage = Match(labels, s.CpuUsage),
                        Temp = Match(labels, s.CpuTemp),
                        Watts = cpuW,
                        CoreClockMhz = Match(labels, s.CpuCoreClock),
                    },
                    Gpu = new GpuPayload
                    {
                        Name = gpuName,
                        Usage = Match(labels, s.GpuUsage),
                        Temp = Match(labels, s.GpuTemp),
                        Watts = gpuW,
                        CoreClockMhz = Match(labels, s.GpuCoreClock),
                        Memory = new MemoryPayload
                        {
                            UsedGb = gpuMemAllocMb is null ? null : gpuMemAllocMb / 1024.0,
                            TotalGb = (gpuMemAllocMb is null || gpuMemAvailMb is null)
                                ? null
                                : (gpuMemAllocMb + gpuMemAvailMb) / 1024.0,
                        },
                    },
                    Memory = new MemoryPayload { TotalGb = memTotalGb, UsedGb = memUsedGb },
                    Storage = new StoragePayload { TotalGb = storageGb },
                    System = new SystemPayload { Watts = SumNullable(cpuW, gpuW) },
                }, AppJsonContext.Default.SensorPayload);
            }
            catch { }

            Thread.Sleep(config.PollMs);
        }
    }

    private static double? Match(Dictionary<string, double> labels, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return null;
        // Exact match wins so e.g. "CPU Package" never grabs "CPU Package Power".
        if (labels.TryGetValue(needle, out var exact)) return exact;
        foreach (var kvp in labels)
            if (kvp.Key.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        return null;
    }

    private static double? SumNullable(double? a, double? b)
        => (a is null && b is null) ? null : (a ?? 0) + (b ?? 0);

    private static Dictionary<string, double> ReadHwInfoLabels()
    {
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.OpenSubKey(VsbKey);
        if (key is null) return dict;

        foreach (var name in key.GetValueNames())
        {
            if (!name.StartsWith("Label", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.GetValue(name) is not string label) continue;
            if (key.GetValue("ValueRaw" + name[5..]) is not string raw) continue;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                dict[label] = v;
        }
        return dict;
    }

    private static string? ReadCpuName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (k?.GetValue("ProcessorNameString") as string)?.Trim();
    }

    // Prefer a discrete GPU over an integrated one when both are present.
    private static string? ReadGpuName()
    {
        using var root = Registry.LocalMachine.OpenSubKey(GpuClassKey);
        if (root is null) return null;

        string? firstFound = null;
        foreach (var sub in root.GetSubKeyNames())
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
            using var k = root.OpenSubKey(sub);
            var desc = (k?.GetValue("DriverDesc") as string)?.Trim();
            if (string.IsNullOrEmpty(desc)) continue;
            firstFound ??= desc;
            if (IsDiscreteGpu(desc)) return desc;
        }
        return firstFound;
    }

    private static bool IsDiscreteGpu(string d) =>
        d.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
        d.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
        d.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
        (d.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            && !d.Contains("Vega", StringComparison.OrdinalIgnoreCase)
            && !d.Contains("Graphics", StringComparison.OrdinalIgnoreCase)) ||
        (d.Contains("Arc", StringComparison.OrdinalIgnoreCase)
            && !d.Contains("HD", StringComparison.OrdinalIgnoreCase)
            && !d.Contains("UHD", StringComparison.OrdinalIgnoreCase));

    // Total comes from SMBIOS (true installed amount); used comes from the OS-visible
    // total minus available, so a 32 GB system reports 32 GB total instead of 31.9.
    private static (double? totalGb, double? usedGb) ReadMemoryStatus()
    {
        var status = new MEMORYSTATUSEX();
        var ok = GlobalMemoryStatusEx(status);
        double? total = GetPhysicallyInstalledSystemMemory(out var kb)
            ? kb / 1048576.0
            : (ok ? status.ullTotalPhys / 1073741824.0 : (double?)null);
        if (!ok) return (total, null);
        return (total, (status.ullTotalPhys - status.ullAvailPhys) / 1073741824.0);
    }

    private static double? TryGetTotalFixedStorageGb()
    {
        double sum = 0;
        var any = false;
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            sum += d.TotalSize / 1073741824.0;
            any = true;
        }
        return any ? sum : null;
    }

    private static Config LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(path)) return new Config();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path),
                AppJsonContext.Default.Config) ?? new Config();
        }
        catch { return new Config(); }
    }
}
