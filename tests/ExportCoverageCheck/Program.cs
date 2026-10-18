using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Text.Json;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: ExportCoverageCheck <project|--features|--validate> <output-directory|video>"); return 2; }
        if (args[0] == "--features") return await FeatureExports.RunAsync(Path.GetFullPath(args[1]),args.ElementAtOrDefault(2));
        if (args[0] == "--validate")
        {
            try { await Validate(Path.GetFullPath(args[1]), 24); return 0; }
            catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        ProjectDocument project = await ProjectFileService.LoadAsync(input);
        foreach (var clip in project.Tracks.SelectMany(track => track.Clips))
        {
            if (args.Contains("--no-transitions")) clip.Transitions.Clear();
            if (args.Contains("--no-opacity")) clip.Keyframes.RemoveAll(key => key.Property == "Opacity");
            string? only = args.FirstOrDefault(value => value.StartsWith("--only-transition="))?.Split('=',2)[1];
            if (only is not null) clip.Transitions.RemoveAll(transition => transition.PresetId != only);
        }
        if (args.Contains("--blur-minimal"))
        {
            foreach (var track in project.Tracks) track.Clips.RemoveAll(clip => !clip.Transitions.Any(transition => transition.PresetId == "transition.blur-dissolve"));
            foreach (var clip in project.Tracks.SelectMany(track=>track.Clips)) { clip.StartSeconds = 0; clip.DurationSeconds = 1; }
        }
        var titles = new Dictionary<string,string>();
        foreach (var clip in project.Tracks.SelectMany(track => track.Clips).Where(clip => clip.Kind == ClipKind.Text))
        {
            string path = Path.Combine(output, clip.Id + ".png");
            await Sta(() =>
            {
                if (DynamicTitlePreset.HasChangingText(clip)) path = DynamicTitleFrames.Render(clip, project.Sequence, Path.Combine(output, clip.Id));
                else TitleBitmapRenderer.Render(clip, project.Sequence, path);
            });
            titles[clip.Id] = path;
        }
        Console.WriteLine($"Exporting {project.Tracks.SelectMany(t=>t.Clips).Count()} clips, {titles.Count} titles, {project.Sequence.Width}x{project.Sequence.Height}");
        try
        {
            double last = -1;
            await TimelineExporter.ExportMp4Async(project, Path.Combine(output,"full-project.mp4"), titles,
                new Progress<double>(value => { if (value - last >= .1) { last = value; Console.WriteLine($"Progress {value:P0}"); } }),
                settings: new VideoExportSettings(VideoBitrateKbps:6500,EndSeconds:args.Contains("--short") ? 1 : 24));
            await Validate(Path.Combine(output,"full-project.mp4"), args.Contains("--short") ? 1 : 24);
            Console.WriteLine("PASS full multi-layer project");
            return 0;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"failure.txt"), exception.ToString());
            Console.Error.WriteLine(exception); return 1;
        }
        finally { await ProjectFileService.ReleaseCachedMediaAsync(project); }
    }
    static async Task Validate(string path, double seconds)
    {
        string encoder=TimelineExporter.ResolveEncoderPath();
        async Task<string> Run(string executable, params string[] arguments)
        {
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(var argument in arguments)start.ArgumentList.Add(argument);
            using var process=Process.Start(start)!;var errors=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();if(process.ExitCode!=0)throw new Exception(await errors);await errors;return await output;
        }
        await Run(encoder,"-v","error","-xerror","-threads","2","-i",path,"-map","0","-f","null","-");
        string metadata=await Run(Path.Combine(Path.GetDirectoryName(encoder)!,"ffprobe.exe"),"-v","error","-show_streams","-show_format","-of","json",path);
        using var json=JsonDocument.Parse(metadata);
        double duration=double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
        var streams=json.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var video=streams.Single(stream=>stream.GetProperty("codec_type").GetString()=="video");
        if(Math.Abs(duration-seconds)>.1||!streams.Any(stream=>stream.GetProperty("codec_type").GetString()=="audio"))throw new Exception("Incomplete output duration or missing audio");
        int frames=int.Parse(video.GetProperty("nb_frames").GetString()!);
        if(frames!=Math.Round(seconds*24)||video.GetProperty("width").GetInt32()!=1280||video.GetProperty("height").GetInt32()!=720)throw new Exception("Incorrect frame count or output dimensions");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!,"validation.json"),metadata);
        Console.WriteLine($"PASS decoded all streams: {frames} frames, {duration:F3}s, 1280x720 with audio");
    }
    static Task Sta(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); result.SetResult(); } catch(Exception error) { result.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return result.Task;
    }
}
