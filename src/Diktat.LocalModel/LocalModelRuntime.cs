using LLama.Native;

namespace Diktat.LocalModel;

/// Settings of llama.cpp for the whole process, made once before the first model loads.
public static class LocalModelRuntime
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static bool _configured;

    /// Where llama.cpp writes its messages (warnings and errors only); the app passes its log.
    public static Action<string>? Log { get; set; }

    /// Threads for generating: the physical cores, roughly. More than that only waits on memory.
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 1, 16);

    /// Loads llama.cpp without a model and says which build it picked, for the smoke test:
    /// proves the native libraries ship with the app and run on this processor.
    public static string CheckNative()
    {
        Configure();
        try
        {
            NativeApi.llama_empty_call();
        }
        catch (Exception ex)
        {
            var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64");
            var files = Directory.Exists(runtimes)
                ? string.Join(", ", Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(AppContext.BaseDirectory, f)).Take(40))
                : "no runtimes folder in " + AppContext.BaseDirectory;
            string log;
            lock (Gate) log = string.Join("\n", Recent);
            throw new InvalidOperationException($"llama.cpp did not load. Picked: {WindowsLibraryFolder() ?? "none"}. Files: {files}\nLoader log:\n{log}", ex);
        }
        return $"llama.cpp loaded, {NativeApi.llama_max_devices()} device(s), GPU offload {(NativeApi.llama_supports_gpu_offload() ? "yes" : "no")}";
    }

    /// The folder of the llama.cpp build for this Windows PC: runtimes/win-arm64/native, or
    /// runtimes/win-x64/native/{avx512,avx2,avx,noavx} by what the processor can do. Null elsewhere.
    internal static string? WindowsLibraryFolder()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes",
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64", "native");
        var candidates = new List<string>();
        if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) candidates.Add("avx512");
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) candidates.Add("avx2");
        if (System.Runtime.Intrinsics.X86.Avx.IsSupported) candidates.Add("avx");
        candidates.Add("noavx");
        candidates.Add("");
        return candidates.Select(c => Path.Combine(native, c)).FirstOrDefault(d => File.Exists(Path.Combine(d, "llama.dll")));
    }

    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured) return;
            _configured = true;
            if (NativeLibraryConfig.LLama.LibraryHasLoaded) return;
            // On Windows the app picks the build itself and loads it by full path: LLamaSharp's own
            // search hands relative paths to the loader, which resolves them against the working
            // directory (wrong in autostart and in the smoke test).
            if (WindowsLibraryFolder() is { } folder)
            {
                try
                {
                    foreach (var dependency in new[] { "ggml-base.dll", "ggml-cpu.dll", "ggml.dll" })
                    {
                        var path = Path.Combine(folder, dependency);
                        if (File.Exists(path)) System.Runtime.InteropServices.NativeLibrary.Load(path);
                    }
                    NativeLibraryConfig.All.WithLibrary(Path.Combine(folder, "llama.dll"), Path.Combine(folder, "mtmd.dll"));
                }
                catch (Exception ex)
                {
                    Recent.Enqueue($"Error: loading {folder} failed: {ex.Message}");
                    Log?.Invoke($"llama.cpp: loading {folder} failed: {ex.Message}");
                }
            }
            NativeLibraryConfig.All
                .WithSearchDirectory(AppContext.BaseDirectory)
                .WithAutoFallback(true)
                .WithLogCallback((level, message) =>
                {
                    if (string.IsNullOrWhiteSpace(message)) return;
                    lock (Gate)
                    {
                        Recent.Enqueue($"{level}: {message.TrimEnd()}");
                        while (Recent.Count > 40) Recent.Dequeue();
                    }
                    if (level is LLamaLogLevel.Warning or LLamaLogLevel.Error) Log?.Invoke("llama.cpp: " + message.TrimEnd());
                });
        }
    }
}
