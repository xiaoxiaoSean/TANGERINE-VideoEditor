using System.Text.Json;
using System.Text.Json.Nodes;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private static BuiltinMusicTrack McpMusicTrack(string id) => BuiltinMusicCatalog.Get(id);

    private static object McpMusicList(JsonObject args)
    {
        string query = McpString(args, "query", "").Trim();
        string genre = McpString(args, "genre", "").Trim();
        string mood = McpString(args, "mood", "").Trim();
        BuiltinMusicTrack[] tracks = BuiltinMusicCatalog.Tracks.Where(track =>
            (genre.Length == 0 || track.Genre.Contains(genre, StringComparison.CurrentCultureIgnoreCase)) &&
            (mood.Length == 0 || track.Mood.Contains(mood, StringComparison.CurrentCultureIgnoreCase)) &&
            (query.Length == 0 || new[] { track.Id, track.Title, track.Artist, track.Genre, track.Mood, track.LicenseId }
                .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase)))).ToArray();
        return new
        {
            total = BuiltinMusicCatalog.Tracks.Count, count = tracks.Length, tracks,
            genres = BuiltinMusicCatalog.Tracks.Select(track => track.Genre).Distinct().Order().ToArray(),
            moods = BuiltinMusicCatalog.Tracks.Select(track => track.Mood).Distinct().Order().ToArray()
        };
    }

    private object McpMusicPreview(JsonObject args)
    {
        BuiltinMusicTrack track = McpMusicTrack(McpString(args, "musicId"));
        _ = ValidateBuiltinMusicTrack(track);
        PreviewBuiltinMusic(track);
        if (_builtinMusicPlayer is null || !ReferenceEquals(_builtinMusicTrack, track))
            throw new StageException("MUSIC0004", "无法开始内置音乐试听，请查看目标实例的状态提示。");
        return new { musicId = track.Id, requested = true, track.AudioPath, track.DurationSeconds,
            previewMode = McpPreviewMode, artworkPath = track.ThumbnailPath,
            artworkLoading = _builtinMusicArtworkLoading, artworkVisible = McpMusicArtworkVisible };
    }

    private async Task<object> McpMusicAddAsync(JsonObject args, CancellationToken token)
    {
        McpEnsureIdle();
        BuiltinMusicTrack music = McpMusicTrack(McpString(args, "musicId"));
        ProjectDocument owner = _project;
        long revision = _projectRevision;
        string? projectPath = _projectPath;
        double start = McpDouble(args, "startSeconds", owner.Sequence.PlayheadSeconds);
        double sourceIn = McpDouble(args, "sourceInSeconds");
        double duration = McpDouble(args, "durationSeconds", music.DurationSeconds - sourceIn);
        double volume = McpDouble(args, "volume", 1);
        double fadeIn = McpDouble(args, "fadeInSeconds"), fadeOut = McpDouble(args, "fadeOutSeconds");
        string? requestedTrackId = args["trackId"]?.GetValue<string>();
        if (!double.IsFinite(music.DurationSeconds) || music.DurationSeconds <= 0 ||
            !double.IsFinite(start) || start < 0 || !double.IsFinite(sourceIn) || sourceIn < 0 ||
            !double.IsFinite(duration) || duration <= 0 || sourceIn + duration > music.DurationSeconds + .000001 ||
            start + duration > 24 * 3600 || !double.IsFinite(volume) || volume is < 0 or > 4 ||
            !double.IsFinite(fadeIn) || fadeIn < 0 || fadeIn > Math.Min(1, duration) ||
            !double.IsFinite(fadeOut) || fadeOut < 0 || fadeOut > Math.Min(1, duration))
            throw new StageException("MCPRT0008", "音乐时间范围、音量或淡入淡出参数无效；不得超出源曲目时长。");
        if (requestedTrackId is not null)
        {
            TimelineTrack requested = McpTrack(requestedTrackId);
            McpWritable(requested);
            if (requested.Kind != TrackKind.Audio)
                throw new StageException("MCPRT0008", "内置音乐只能插入音频轨道。");
        }
        string[] projected = owner.Media.SelectMany(asset => new[] { asset.SourcePath, asset.ProxyPath })
            .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().Append(music.AudioPath).ToArray();
        if (projectPath is null) await ProjectFileService.EnsureImportCapacityAsync(projected, token);
        else await ProjectFileService.EnsureImportCapacityAsync(projectPath, projected, token);
        token.ThrowIfCancellationRequested();
        McpRequireCurrent(owner, revision);
        if (projectPath != _projectPath) throw new StageException("MCPRT0003", "空间检查期间项目保存位置已改变，请重试。");

        TimelineClip? inserted = null;
        TimelineTrack? destination = null;
        MediaAsset? imported = null;
        McpMutate("添加内置音乐", () =>
        {
            destination = requestedTrackId is not null ? McpTrack(requestedTrackId) :
                _selectedTrack is { Kind: TrackKind.Audio, IsLocked: false } selected && owner.Tracks.Contains(selected)
                    ? selected : owner.Tracks.FirstOrDefault(track => track.Kind == TrackKind.Audio && !track.IsLocked);
            if (destination is null)
            {
                destination = new TimelineTrack { Name = "音乐", Kind = TrackKind.Audio };
                owner.Tracks.Add(destination); McpReorderTracks();
            }
            McpWritable(destination);
            if (destination.Kind != TrackKind.Audio) throw new StageException("MCPRT0008", "目标轨道已不再是音频轨道。");
            double position = McpBool(args, "snap", true) && _snapEnabled && destination.IsSnapping
                ? SnapNewClipStart(destination, SnapEditTime(start)) : start;
            if (position + duration > 24 * 3600) throw new StageException("MCPRT0008", "音乐吸附位置超出时间线范围。");
            imported = EnsureBuiltinMusicAsset(music);
            inserted = new TimelineClip
            {
                Kind = ClipKind.Audio, Name = music.Title, MediaAssetId = imported.Id,
                StartSeconds = position, DurationSeconds = duration, SourceInSeconds = sourceIn, Volume = volume
            };
            new AudioSettings { Category = "音乐" }.Save(inserted);
            if (fadeIn > 0 || fadeOut > 0)
            {
                inserted.AdditionalData ??= [];
                inserted.AdditionalData[AudioProcessing.RemixFadeInKey] = JsonSerializer.SerializeToElement(fadeIn);
                inserted.AdditionalData[AudioProcessing.RemixFadeOutKey] = JsonSerializer.SerializeToElement(fadeOut);
            }
            destination.Clips.Add(inserted);
            SelectClip(inserted);
        });
        return new
        {
            musicId = music.Id, assetId = imported!.Id, clipId = inserted!.Id, trackId = destination!.Id,
            inserted.StartSeconds, inserted.DurationSeconds, inserted.SourceInSeconds, inserted.Volume,
            music.Artist, music.Genre, music.Mood, music.SourceUrl, music.LicenseId, music.LicenseUrl
        };
    }
}
