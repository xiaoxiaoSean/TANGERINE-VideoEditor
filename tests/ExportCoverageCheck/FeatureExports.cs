using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

internal static class FeatureExports
{
    static string Work = "", Video = "", ImagePath = "", Audio = "";
    static int Passed;
    static string? Filter;
    static readonly List<string> Failures = [];
    static readonly List<object> Results = [];
    public static async Task<int> RunAsync(string directory,string? filter=null)
    {
        Filter=filter;
        Work = directory;Directory.CreateDirectory(Work);
        Video = Path.Combine(Work,"source.mp4");Audio=Path.Combine(Work,"source.wav");ImagePath=Path.Combine(Work,"source.png");
        await Ffmpeg("-f","lavfi","-i","testsrc2=s=160x90:r=12:d=3","-f","lavfi","-i","sine=frequency=440:duration=3",
            "-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-shortest",Video);
        await Ffmpeg("-i",Video,"-frames:v","1",ImagePath);
        await Ffmpeg("-i",Video,"-vn","-c:a","pcm_s16le",Audio);
        foreach (var kind in new[]{ClipKind.Video,ClipKind.Image,ClipKind.Audio,ClipKind.Color,ClipKind.Text,ClipKind.Adjustment})
            await Check("objects/"+kind,()=>{var (p,c)=Fixture();if(kind==ClipKind.Adjustment){var a=new TimelineClip{Kind=kind,DurationSeconds=1};a.Effects.Add(new(){PresetId="effect.contrast"});p.Tracks.Insert(0,new(){Order=-1,Clips=[a]});}else{c.Kind=kind;if(kind==ClipKind.Image)c.MediaAssetId="image";if(kind==ClipKind.Audio){p.Tracks[0].Clips.Clear();p.Tracks[1].Clips.Add(c);c.MediaAssetId="audio";}if(kind==ClipKind.Text){c.Text=new(){Content="导出验证",FontSize=18};}}return p;});
        foreach(var mode in new[]{"Normal","Multiply","Screen","Overlay","Add","Darken","Lighten","Difference","SoftLight","HardLight"})
            await Check("blend/"+mode,()=>{var(p,c)=Fixture();c.BlendMode=mode;return p;});
        foreach(var shape in new[]{"Rectangle","Ellipse","Path"})foreach(bool invert in new[]{false,true})
            await Check($"mask/{shape}/{invert}",()=>{var(p,c)=Fixture();c.Mask=new(){Shape=shape,Feather=.1,Invert=invert,Points=[new(){X=.1,Y=.1},new(){X=.9,Y=.2},new(){X=.5,Y=.9}]};return p;});
        foreach(var interpolation in new[]{"Sampling","Blend","OpticalFlow"})foreach(bool reverse in new[]{false,true})
            await Check($"speed/{interpolation}/{reverse}",()=>{var(p,c)=Fixture();c.Speed=1.3;c.IsReversed=reverse;c.FrameInterpolation=interpolation;return p;});
        await Check("speed/remap",()=>{var(p,c)=Fixture();c.TimeRemapPoints=[new(){TimeSeconds=0,Speed=.7},new(){TimeSeconds=.4,Speed=1.8}];return p;});
        await Check("speed/remap-reverse",()=>{var(p,c)=Fixture();c.IsReversed=true;c.TimeRemapPoints=[new(){TimeSeconds=0,Speed=.7},new(){TimeSeconds=.4,Speed=1.8}];return p;});
        foreach(var property in ClipAnimation.Properties.Select(x=>x.Id).Concat(new[]{"Mask.CenterX","Mask.CenterY"}))
            await Check("keyframes/"+property,()=>{var(p,c)=Fixture();c.Mask=new();c.Keyframes=[new(){Property=property,TimeSeconds=0,Value=property.StartsWith("Scale")||property=="Volume"?1:property=="Opacity"?.2:0,Interpolation="EaseInOut"},new(){Property=property,TimeSeconds=1,Value=property.StartsWith("Scale")?1.2:property=="Opacity"||property=="Volume"?1:property.StartsWith("Mask")?.6:10}];return p;});
        await Check("transform/crop-anchor-rotate",()=>{var(p,c)=Fixture();c.Transform=new(){CropLeft=.12,CropRight=.08,CropTop=.1,RotationDegrees=23,AnchorX=.2,AnchorY=.8,ScaleX=.8,ScaleY=.9,PositionX=8,PositionY=-4};return p;});
        foreach(string channel in new[]{"立体声","单声道","仅左声道","仅右声道"})
            await Check("audio/channels/"+channel,()=>AudioProject(new(){ChannelMode=channel,Pan=-.4,Gain=.8}));
        await Check("audio/high-low-pass",()=>AudioProject(new(){HighPassHz=100,LowPassHz=8000}));
        await Check("audio/equalizer",()=>AudioProject(new(){EqualizerHz=1000,EqualizerGainDb=6}));
        await Check("audio/compressor",()=>AudioProject(new(){CompressorEnabled=true}));
        await Check("audio/limiter",()=>AudioProject(new(){LimiterEnabled=true}));
        await Check("audio/denoise",()=>AudioProject(new(){NoiseReductionEnabled=true}));
        await Check("audio/echo",()=>AudioProject(new(){EchoEnabled=true}));
        await Check("audio/all-processing",()=>AudioProject(new(){HighPassHz=80,LowPassHz=10000,EqualizerHz=1000,EqualizerGainDb=2,CompressorEnabled=true,LimiterEnabled=true,NoiseReductionEnabled=true,EchoEnabled=true,Pan=.4}));
        await Check("audio/track-settings",()=>{var p=AudioProject(new());new AudioSettings{Pan=.3,Gain=.7,CompressorEnabled=true}.Save(p.Tracks[0]);return p;});
        await Check("audio/remix-fades",()=>{var p=AudioProject(new());p.Tracks[0].Clips[0].AdditionalData=new(){[AudioProcessing.RemixFadeInKey]=JsonSerializer.SerializeToElement(.2),[AudioProcessing.RemixFadeOutKey]=JsonSerializer.SerializeToElement(.3)};return p;});
        await Check("audio/ducking",()=>{var p=AudioProject(new(){Category="音乐",AutoDuckingEnabled=true});var voice=new TimelineClip{Kind=ClipKind.Audio,MediaAssetId="audio",StartSeconds=.2,DurationSeconds=.5};new AudioSettings{Category="对白"}.Save(voice);p.Tracks[1].Clips.Add(voice);return p;});
        foreach(var preset in BuiltinCatalog.Stickers)
            await Check("sticker/"+preset.Id,()=>{var(p,c)=Fixture();c.Kind=ClipKind.Text;c.Text=new(){Content=""};GraphicShape.FromPreset(preset,160,90).Save(c);return p;});
        await Check("title/full-style",()=>{var(p,c)=Fixture();c.Kind=ClipKind.Text;c.Text=new(){Content="多行文字\nTitle",FontSize=18,LetterSpacing=2,LineHeight=1.5,Bold=true,Italic=true,Underline=true,ForegroundColor="#FFAA66",BackgroundColor="#332244",BackgroundPaddingX=3,BackgroundPaddingY=2,BackgroundCornerRadius=3,BackgroundOpacity=.7,OutlineEnabled=true,OutlineThickness=1,ShadowEnabled=true,ShadowBlur=2,GlowRadius=3};return p;});
        await Check("nested/sequence",()=>{var(p,c)=Fixture();var w=new ProjectSequenceWorkspace{Sequence=new(){Width=160,Height=90,FramesPerSecond=12},Tracks=p.Tracks,Markers=[]};p.Sequences=[w];p.Tracks=[new(){Clips=[new(){Kind=ClipKind.Sequence,NestedSequenceId=w.Sequence.Id,DurationSeconds=1}]}];return SequenceFlattener.Flatten(p);});
        await Check("track/hidden-muted",()=>{var(p,c)=Fixture();p.Tracks[0].IsHidden=true;p.Tracks[0].IsMuted=true;return p;});
        await Check("export/size-rate-range",FixtureProject,new VideoExportSettings(128,72,10,1000,.2,.8));
        await Check("export/sub-frame-range",FixtureProject,new VideoExportSettings(StartSeconds:.501,EndSeconds:.502));
        await Check("export/sub-frame-at-end",FixtureProject,new VideoExportSettings(StartSeconds:.999,EndSeconds:1));
        await Check("color/rec709-to601",()=>{var(p,c)=Fixture();p.Sequence.OutputColorSpace="Rec601";return p;});
        // The previous smoke tests began every transition at zero on tiny
        // canvases. Keep a delayed blur in a larger, multi-overlay graph to
        // exercise framesync requesting future branches before they are shown.
        await Check("regression/delayed-blur-multilayer",()=>
        {
            var(p,c)=Fixture();p.Sequence.Width=640;p.Sequence.Height=360;c.DurationSeconds=1;
            for(int i=0;i<6;i++){var n=new TimelineClip{Kind=ClipKind.Image,MediaAssetId="image",StartSeconds=i+1,DurationSeconds=1};n.Transitions.Add(new(){PresetId="transition.blur-dissolve",Edge="In",DurationSeconds=.25});p.Tracks[0].Clips.Add(n);}
            for(int i=0;i<12;i++)p.Tracks.Insert(0,new(){Order=-i-1,Clips=[new(){Kind=ClipKind.Text,StartSeconds=i*.4,DurationSeconds=.5,Text=new(){Content="Overlay "+i,FontSize=20}}]});return p;
        },new VideoExportSettings(EndSeconds:1));
        // Match the fractional 4.8-second chapter boundaries and the delayed
        // blur branch of the original native-crash reproducer at 24 fps.
        await Check("regression/fractional-held-frame",()=>
        {
            var(p,c)=Fixture();p.Sequence.FramesPerSecond=24;c.Kind=ClipKind.Image;c.MediaAssetId="image";c.DurationSeconds=4.8;
            for(int i=1;i<5;i++)
            {
                var next=new TimelineClip{Kind=ClipKind.Image,MediaAssetId="image",StartSeconds=i*4.8,DurationSeconds=4.8};
                next.Transitions.Add(new(){PresetId=i==4?"transition.blur-dissolve":"transition.fade",Edge="In",DurationSeconds=.6});
                p.Tracks[0].Clips.Add(next);
            }
            for(int i=0;i<20;i++)p.Tracks.Insert(0,new(){Order=-i-1,Clips=[new(){Kind=ClipKind.Text,StartSeconds=(i/4)*4.8+.2,DurationSeconds=4.4,Text=new(){Content="Layer "+i,FontSize=12}}]});
            return p;
        },new VideoExportSettings(EndSeconds:1));
        await File.WriteAllTextAsync(Path.Combine(Work,"results.json"),JsonSerializer.Serialize(new{passed=Passed,failures=Failures,results=Results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"FEATURE EXPORTS: {Passed} passed, {Failures.Count} failed");
        return Failures.Count==0?0:1;
    }
    static ProjectDocument FixtureProject()=>Fixture().Item1;
    static (ProjectDocument,TimelineClip) Fixture()
    {
        var p=ProjectDocument.CreateDefault();p.Sequence.Width=160;p.Sequence.Height=90;p.Sequence.FramesPerSecond=12;
        p.Media=[new(){Id="video",Kind=MediaKind.Video,SourcePath=Video,HasAudio=true,Width=160,Height=90,DurationSeconds=3,FramesPerSecond=12},new(){Id="image",Kind=MediaKind.Image,SourcePath=ImagePath,Width=160,Height=90},new(){Id="audio",Kind=MediaKind.Audio,SourcePath=Audio,HasAudio=true,DurationSeconds=3}];
        var c=new TimelineClip{Kind=ClipKind.Video,MediaAssetId="video",DurationSeconds=1,Color="#4488CC"};p.Tracks[0].Clips.Add(c);return(p,c);
    }
    static ProjectDocument AudioProject(AudioSettings settings){var(p,c)=Fixture();settings.Save(c);return p;}
    static async Task Check(string name,Func<ProjectDocument> create,VideoExportSettings? settings=null)
    {
        if(Filter is not null&&!name.StartsWith(Filter,StringComparison.Ordinal))return;
        string output=Path.Combine(Work,name.Replace('/','-')+".mp4");
        try
        {
            ProjectDocument p=create();var titles=new Dictionary<string,string>();
            foreach(var c in p.Tracks.SelectMany(t=>t.Clips).Where(c=>c.Kind==ClipKind.Text))
            {string path=Path.Combine(Work,c.Id+".png");await Sta(()=>TitleBitmapRenderer.Render(c,p.Sequence,path));titles[c.Id]=path;}
            var preview=await CompositeFrameRenderer.RenderFrameAsync(p,.35,160,90,previewGpuMode:"CPU 软件渲染");
            if(preview.PixelWidth!=160||preview.PixelHeight!=90)throw new Exception("Invalid preview dimensions");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await TimelineExporter.ExportMp4Async(p,output,titles,cancellationToken:timeout.Token,settings:settings);
            await Ffmpeg("-i",output,"-f","null","-");
            if(new FileInfo(output).Length<100)throw new Exception("Empty output");
            double end=settings?.EndSeconds??p.Tracks.SelectMany(t=>t.Clips).Max(c=>c.StartSeconds+c.DurationSeconds);
            int expected=Math.Max(1,(int)Math.Ceiling((end-(settings?.StartSeconds??0))*(settings?.FramesPerSecond??p.Sequence.FramesPerSecond)-.000001));
            var probe=new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(TimelineExporter.ResolveEncoderPath())!,"ffprobe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"-v","error","-select_streams","v:0","-show_entries","stream=nb_frames","-of","csv=p=0",output})probe.ArgumentList.Add(arg);
            using(var process=Process.Start(probe)!)
            {
                var errors=process.StandardError.ReadToEndAsync();string count=await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();
                if(process.ExitCode!=0||!int.TryParse(count.Trim(),out int actual)||actual!=expected)throw new Exception($"Incorrect frame count: expected {expected}, actual {count.Trim()}. {await errors}");
                await errors;
            }
            Passed++;Console.WriteLine("PASS "+name);Results.Add(new{name,passed=true,output});
        }
        catch(Exception e){Failures.Add(name+": "+e);Results.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine("FAIL "+name+": "+e.Message);}
    }
    static async Task Ffmpeg(params string[] args)
    {
        var psi=new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{"-hide_banner","-loglevel","error","-y","-threads","2"}.Concat(args))psi.ArgumentList.Add(arg);
        using var process=Process.Start(psi)!;var error=process.StandardError.ReadToEndAsync();var stdout=process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();await stdout;if(process.ExitCode!=0)throw new Exception(await error);
    }
    static Task Sta(Action a){var tcs=new TaskCompletionSource();var t=new Thread(()=>{try{a();tcs.SetResult();}catch(Exception e){tcs.SetException(e);}});t.SetApartmentState(ApartmentState.STA);t.Start();return tcs.Task;}
}
