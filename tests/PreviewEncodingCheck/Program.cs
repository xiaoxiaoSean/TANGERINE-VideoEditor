using System.Diagnostics;
using System.IO;
using TANGERINE_VideoEditor.Editor;

// The probe launches this executable to exercise a stuck driver without
// depending on a particular GPU vendor or a broken local driver install.
if (args.Contains("-hide_banner"))
{
    return await IntegrationChecks.RunEncoderChildAsync(args);
}

int checks = 0;
void Check(bool passed, string name)
{
    if (!passed) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine($"PASS {name}");
}

string ffmpeg = args.FirstOrDefault() ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".nuget", "packages", "ffmpeg.static", "4.4.0.7", "ffmpeg", "ffmpeg.exe");

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    bool observed = false;
    try { await PreviewEncodingProfile.SelectAsync(ffmpeg, cancelled.Token); }
    catch (OperationCanceledException) { observed = true; }
    Check(observed, "pre-cancelled selection stops without a probe");
}

var selectionTimer = Stopwatch.StartNew();
PreviewEncodingProfile[] concurrent = await Task.WhenAll(
    Enumerable.Range(0, 6).Select(_ => PreviewEncodingProfile.SelectAsync(ffmpeg)));
selectionTimer.Stop();
Check(concurrent.All(profile => ReferenceEquals(profile, concurrent[0])), "concurrent requests share one profile");
PreviewEncodingProfile available = concurrent[0];
Console.WriteLine($"Selected {available.EncoderName}; initial probes {selectionTimer.Elapsed.TotalSeconds:F3}s");
Check(available.VideoArguments.Contains("-c:v"), "selected profile carries structured video arguments");
Check(available.EncoderName == "libx264" || available.IsHardwareAccelerated,
    "hardware result is proven by an actual MP4 encode");
var cacheTimer = Stopwatch.StartNew();
Check(ReferenceEquals(available, await PreviewEncodingProfile.SelectAsync(ffmpeg)), "warm selection reuses completed probe");
cacheTimer.Stop();
Check(cacheTimer.Elapsed < TimeSpan.FromSeconds(.5), "warm selection does not launch another encoder");

PreviewEncodingProfile missing = await PreviewEncodingProfile.SelectAsync(
    Path.Combine(Path.GetTempPath(), $"ffmpeg-missing-{Guid.NewGuid():N}.exe"));
Check(ReferenceEquals(missing, PreviewEncodingProfile.Software), "missing encoder probe chooses software without throwing");

if (available.IsHardwareAccelerated)
{
    available.MarkUnavailable();
    Check(ReferenceEquals(await PreviewEncodingProfile.SelectAsync(ffmpeg), PreviewEncodingProfile.Software),
        "failed real hardware session is not offered again");
}

string testExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable");
var timeoutTimer = Stopwatch.StartNew();
Task<PreviewEncodingProfile> shared = PreviewEncodingProfile.SelectAsync(testExecutable);
using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
{
    bool observed = false;
    try { await PreviewEncodingProfile.SelectAsync(testExecutable, cancellation.Token); }
    catch (OperationCanceledException) { observed = true; }
    Check(observed, "caller can stop waiting for a shared probe");
    Check(!shared.IsCompleted, "one cancelled caller does not cancel the shared probe");
}
Check(ReferenceEquals(await shared, PreviewEncodingProfile.Software), "stuck driver falls back after the bounded probe");
timeoutTimer.Stop();
Check(timeoutTimer.Elapsed < TimeSpan.FromSeconds(7), "all three driver probes share a five-second bound");
string processName = Process.GetCurrentProcess().ProcessName;
Check(Process.GetProcessesByName(processName).Count(process => process.Id != Environment.ProcessId) == 0,
    "timed-out probe child processes are killed");
Console.WriteLine($"Passed {checks} checks; timeout test {timeoutTimer.Elapsed.TotalSeconds:F3}s");
int integrationArgument = Array.IndexOf(args, "--integration");
if (integrationArgument >= 0 && integrationArgument + 1 < args.Length)
    await IntegrationChecks.RunAsync(args[integrationArgument + 1], ffmpeg);
return 0;
