using LLama.Native;

namespace Diktat.LocalModel;

/// Settings of llama.cpp for the whole process, made once before the first model loads.
public static class LocalModelRuntime
{
    private static readonly object Gate = new();
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
        NativeApi.llama_empty_call();
        return $"llama.cpp loaded, {NativeApi.llama_max_devices()} device(s), GPU offload {(NativeApi.llama_supports_gpu_offload() ? "yes" : "no")}";
    }

    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured) return;
            _configured = true;
            if (NativeLibraryConfig.LLama.LibraryHasLoaded) return;
            NativeLibraryConfig.All
                .WithAutoFallback(true)
                .WithLogCallback((level, message) =>
                {
                    if (level is LLamaLogLevel.Warning or LLamaLogLevel.Error && !string.IsNullOrWhiteSpace(message))
                        Log?.Invoke("llama.cpp: " + message.TrimEnd());
                });
        }
    }
}
