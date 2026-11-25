using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Audio.OpenAL;
using Thrust.Audio;

namespace SharpFAI.Player;

internal static class PlaybackAudio
{
    internal const string ProbeArgument = "--probe-audio";
    private const string DriversVariable = "ALSOFT_DRIVERS";
    private const int ProbeTimeoutMs = 3000;
    private static Assembly? _entryPoint;
    private static bool? _deviceAvailable;
    private static bool _disabled;

    internal static void Configure(Assembly entryPoint, bool disabled = false)
    {
        _entryPoint = entryPoint;
        _disabled = disabled;
    }

    internal static bool Initialize()
    {
        if (_disabled) return false;
        if (AudioEngine.IsInitialized) return true;

        if (_entryPoint is { } entryPoint && OperatingSystem.IsWindows() && _deviceAvailable is null)
            _deviceAvailable = SelectWindowsBackend(entryPoint);
        return _deviceAvailable != false && AudioEngine.Initialize();
    }

    internal static int RunProbe()
    {
        if (OperatingSystem.IsWindows())
        {
            // A driver fault must exit the probe without displaying a Windows error dialog.
            SetErrorMode(0x0001 | 0x0002); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX
            PreferBundledOpenAl();
        }
        bool available = AudioEngine.Initialize();
        AudioEngine.Shutdown();
        return available ? 0 : 1;
    }

    private static bool SelectWindowsBackend(Assembly entryPoint)
    {
        PreferBundledOpenAl();
        if (Probe(entryPoint, drivers: null)) return true;

        foreach (string drivers in new[] { "dsound", "winmm" })
        {
            if (!Probe(entryPoint, drivers)) continue;
            // OpenAL Soft reads the UCRT environment, which is separate from the Win32 environment.
            if (SetCrtEnvironment(DriversVariable, drivers) != 0) continue;
            Console.Error.WriteLine($"[Audio] Using OpenAL compatibility backend: {drivers}.");
            return true;
        }

        Console.Error.WriteLine("[Audio] 音频设备不可用，已关闭实时音频 / Audio output unavailable. Continuing without live audio.");
        return false;
    }

    private static void PreferBundledOpenAl()
    {
        // For a single-file app the native libraries are extracted outside the executable directory.
        string? nativePaths = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;
        string[] directories = nativePaths?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        string baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var search = Assembly.GetEntryAssembly()?.Location == ""
            ? directories.Where(dir => !string.Equals(Path.TrimEndingDirectorySeparator(dir), baseDirectory,
                StringComparison.OrdinalIgnoreCase)).Append(baseDirectory)
            : directories.Prepend(baseDirectory);
        foreach (string directory in search)
        {
            string path = Path.Combine(directory, "OpenAL32.dll");
            if (!File.Exists(path)) continue;
            OpenALLibraryNameContainer.OverridePath = path;
            return;
        }
    }

    private static bool Probe(Assembly entryPoint, string? drivers)
    {
        if (Environment.ProcessPath is not { } executable) return false;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(entryPoint.Location);
        start.ArgumentList.Add(ProbeArgument);
        if (drivers is not null) start.Environment[DriversVariable] = drivers;

        string backend = drivers ?? "default";
        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return false;
            if (process.WaitForExit(ProbeTimeoutMs))
            {
                if (process.ExitCode == 0) return true;
                Console.Error.WriteLine($"[Audio] OpenAL probe ({backend}) exited with 0x{process.ExitCode:X8}.");
                return false;
            }

            if (!process.HasExited) process.Kill();
            Console.Error.WriteLine($"[Audio] OpenAL probe ({backend}) timed out.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"[Audio] OpenAL probe ({backend}) failed: {error.Message}");
        }
        return false;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("ucrtbase.dll", EntryPoint = "_wputenv_s", CharSet = CharSet.Unicode,
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetCrtEnvironment(string name, string value);
}
