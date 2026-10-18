using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

internal static class IntegrationChecks
{
    private const string ChildMode = "TANGERINE_ENCODING_CHECK_MODE";
    private const string ChildLog = "TANGERINE_ENCODING_CHECK_LOG";
    private const string ChildEncoder = "TANGERINE_ENCODING_CHECK_ENCODER";

    public static async Task<int> RunEncoderChildAsync(string[] arguments)
    {
        string? mode = Environment.GetEnvironmentVariable(ChildMode);
        if (string.IsNullOrEmpty(mode))
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 1;
        }
        File.AppendAllText(Environment.GetEnvironmentVariable(ChildLog)!, $"{Environment.ProcessId}\n");
        if (mode == "cancel")
        {
            Console.WriteLine("out_time_ms=1");
            Console.Out.Flush();
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 1;
        }

        // Corrupt the actual shared graph, then forward both attempted
        // encodes to real FFmpeg. Neither codec can repair this failure.
        int scriptArgument = Array.IndexOf(arguments, "-filter_complex_script");
        File.WriteAllText(arguments[scriptArgument + 1], "[missing]null[out]");
        using var child = new Process { StartInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable(ChildEncoder)!)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        } };
        foreach (string argument in arguments) child.StartInfo.ArgumentList.Add(argument);
        child.Start();
        Task output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        Task error = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        await child.WaitForExitAsync();
        await Task.WhenAll(output, error);
        return child.ExitCode;
    }

    public static async Task RunAsync(string assemblyPath, string ffmpeg)
    {
        Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        Type projectType = assembly.GetType("TANGERINE_VideoEditor.Project.ProjectDocument")!;
        Type profileType = assembly.GetType("TANGERINE_VideoEditor.Editor.PreviewEncodingProfile")!;
        Type exporterType = assembly.GetType("TANGERINE_VideoEditor.Editor.TimelineExporter")!;
        MethodInfo exporter = exporterType.GetMethod("ExportMp4Async")!;
        ConstructorInfo profileConstructor = profileType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        object failedProfiles = profileType.GetField("FailedProfiles", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        MethodInfo containsFailed = failedProfiles.GetType().GetMethod("ContainsKey")!;
        string testDirectory = Path.Combine(Path.GetTempPath(), $"tangerine-fallback-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        int checks = 0;
        void Check(bool result, string name)
        {
            if (!result) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine($"PASS {name}");
        }

        object Project() => JsonSerializer.Deserialize("""
            {"Sequence":{"Width":160,"Height":90,"FramesPerSecond":10},
             "Tracks":[{"Kind":0,"Clips":[{"Kind":4,"Color":"#339966","DurationSeconds":0.4}]}]}
            """, projectType)!;
        object Profile(string key, bool brokenCodec) => profileConstructor.Invoke([
            "test_gpu", "Test GPU", true,
            brokenCodec ? new[] { "-c:v", "h264_intentionally_missing" } :
                new[] { "-c:v", "libx264", "-preset", "ultrafast" }, key]);
        bool Failed(string key) => (bool)containsFailed.Invoke(failedProfiles, [key])!;
        async Task Export(string output, object profile, string encoder, CancellationToken token = default,
            IProgress<double>? progress = null)
        {
            var task = (Task)exporter.Invoke(null, [Project(), output,
                new Dictionary<string, string>(), progress, token, null, profile, encoder])!;
            await task;
        }

        string? oldMode = Environment.GetEnvironmentVariable(ChildMode);
        string? oldLog = Environment.GetEnvironmentVariable(ChildLog);
        string? oldEncoder = Environment.GetEnvironmentVariable(ChildEncoder);
        try
        {
            string fallbackKey = Guid.NewGuid().ToString("N");
            string output = Path.Combine(testDirectory, "fallback.mp4");
            await Export(output, Profile(fallbackKey, brokenCodec: true), ffmpeg);
            Check(File.Exists(output) && new FileInfo(output).Length > 0,
                "actual exporter retries failed GPU codec using software");
            Check(Failed(fallbackKey), "successful software retry disables the failed hardware session");

            string testExecutable = Environment.ProcessPath!;
            string attempts = Path.Combine(testDirectory, "attempts.txt");
            string graphKey = Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(ChildMode, "graph-failure");
            Environment.SetEnvironmentVariable(ChildLog, attempts);
            Environment.SetEnvironmentVariable(ChildEncoder, ffmpeg);
            Exception? graphFailure = null;
            try { await Export(Path.Combine(testDirectory, "broken.mp4"), Profile(graphKey, false), testExecutable); }
            catch (Exception exception) { graphFailure = exception; }
            Check(graphFailure?.GetType().Name == "ExportException" &&
                graphFailure.GetType().GetProperty("StageCode")!.GetValue(graphFailure)!.ToString() == "Encode",
                "shared malformed graph surfaces Encode stage after software also fails");
            Check(File.ReadAllLines(attempts).Length == 2,
                "malformed graph exercises one hardware and one software attempt");
            Check(!Failed(graphKey), "shared graph failure does not poison GPU capability cache");

            File.Delete(attempts);
            string cancellationKey = Guid.NewGuid().ToString("N");
            string cancelledOutput = Path.Combine(testDirectory, "cancelled.mp4");
            Environment.SetEnvironmentVariable(ChildMode, "cancel");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool cancelled = false;
            var progress = new CallbackProgress(_ => cancellation.Cancel());
            try { await Export(cancelledOutput, Profile(cancellationKey, false), testExecutable,
                cancellation.Token, progress); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancelling an active encoder preserves cancellation");
            Check(File.ReadAllLines(attempts).Length == 1,
                "cancellation does not launch a software retry");
            Check(!Failed(cancellationKey), "cancellation does not disable GPU capability");
            Check(!File.Exists(cancelledOutput) && !Directory.EnumerateFiles(testDirectory, ".*.mp4").Any(),
                "failed and cancelled attempts leave no partial output");
            Console.WriteLine($"Passed {checks} exporter fallback integration checks using {assemblyPath}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChildMode, oldMode);
            Environment.SetEnvironmentVariable(ChildLog, oldLog);
            Environment.SetEnvironmentVariable(ChildEncoder, oldEncoder);
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }
}
