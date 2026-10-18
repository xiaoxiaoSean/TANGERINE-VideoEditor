using System.Diagnostics;
using System.Globalization;
using TANGERINE_VideoEditor.Editor;

int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL " + description);
    Console.WriteLine("PASS " + description);
    checks++;
}

var parsed = SceneCutDetector.ParseMetadata("""
frame:0 pts:15360 pts_time:1.5
lavfi.scene_score=0.75
frame: 1 pts:30720 pts_time:3.0
lavfi.scene_score=9.5e-1
""", 2);
Check(parsed.Count == 2, "Multiline frame headers are paired with scene scores");
Check(parsed[0].TimeSeconds == 3.5 && parsed[1].TimeSeconds == 5, "Seek offsets produce absolute cut times");
Check(parsed[1].FrameNumber == 1 && Math.Abs(parsed[1].Score - .95) < .000001, "Whitespace and scientific notation parse correctly");
var inline = SceneCutDetector.ParseMetadata("frame:2 pts:9 pts_time:0.9 lavfi.scene_score=0.8");
Check(inline.Count == 1 && inline[0].TimeSeconds == .9, "Inline metadata remains supported");
Check(SceneCutDetector.ParseMetadata("lavfi.scene_score=0.8").Count == 0, "Orphan scores cannot create fabricated cut times");
Check(SceneCutDetector.ParseMetadata("frame:0 pts:1 pts_time:0.1\nnoise\nframe:1 pts:2 pts_time:bad\nlavfi.scene_score=0.8").Count == 0,
    "An invalid newer header cannot inherit a previous frame timestamp");

string output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "tangerine-scene-check-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output);
string fixture = Path.Combine(output, "two-scenes.mp4");
string encoder = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
var info = new ProcessStartInfo(encoder)
{
    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
};
foreach (string argument in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", "color=black:s=96x64:r=10:d=2", "-f", "lavfi", "-i",
    "color=white:s=96x64:r=10:d=2", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]",
    "-c:v", "libx264", "-pix_fmt", "yuv420p", fixture }) info.ArgumentList.Add(argument);
using (Process process = Process.Start(info)!)
{
    Task<string> error = process.StandardError.ReadToEndAsync();
    Task<string> standard = process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    await Task.WhenAll(error, standard);
    Check(process.ExitCode == 0 && File.Exists(fixture), "Bundled FFmpeg produces a real black-to-white fixture: " + await error);
}
CultureInfo originalCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    var result = await SceneCutDetector.DetectSceneCutsAsync(fixture, .2);
    Check(result.Cuts.Count == 1, "Actual two-scene video yields exactly one cut");
    Check(Math.Abs(result.Cuts[0].TimeSeconds - 2) < .11 && result.Cuts[0].Score > .8,
        "Actual scene cut has correct time and nonzero measured score");
    Check(Math.Abs(result.TotalDuration - 4) < .11, "Source duration is parsed under a comma-decimal culture");
    var ranged = await SceneCutDetector.DetectSceneCutsAsync(fixture, .2, startSeconds: 1, durationSeconds: 2);
    Check(ranged.Cuts.Count == 1 && Math.Abs(ranged.Cuts[0].TimeSeconds - 2) < .11,
        "Ranged scan retains the absolute two-second scene boundary");
    var constant = await SceneCutDetector.DetectSceneCutsAsync(fixture, .2, durationSeconds: 1);
    Check(constant.Cuts.Count == 0, "A truly constant scene still returns no cuts");
}
finally { CultureInfo.CurrentCulture = originalCulture; }
Console.WriteLine($"PASS {checks} scene-cut checks; fixture: {fixture}");
