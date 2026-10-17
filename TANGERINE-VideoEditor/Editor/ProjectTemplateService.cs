using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Project template system for creating new projects from predefined configurations.
/// Templates include sequence settings, tracks, and placeholder content.
/// </summary>
public static class ProjectTemplateService
{
    public sealed class ProjectTemplate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "无标题模板";
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = "通用";
        public string ThumbnailPath { get; set; } = string.Empty;

        // Sequence preset
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public double FramesPerSecond { get; set; } = 30;
        public int AudioSampleRate { get; set; } = 48000;

        // Tracks configuration
        public List<TrackTemplate> Tracks { get; set; } = new();

        // Default effects and settings
        public Dictionary<string, JsonElement> Settings { get; set; } = new();
    }

    public sealed class TrackTemplate
    {
        public string Name { get; set; } = string.Empty;
        public TrackKind Kind { get; set; }
        public int Order { get; set; }
    }

    private static readonly List<ProjectTemplate> _builtinTemplates = new()
    {
        // 16:9 HD Templates
        new ProjectTemplate
        {
            Id = "hd_1080p_30fps",
            Name = "高清 1080p (30fps)",
            Description = "标准高清视频，适合 YouTube、B站",
            Category = "16:9",
            Width = 1920, Height = 1080, FramesPerSecond = 30,
            Tracks = new()
            {
                new() { Name = "视频 1", Kind = TrackKind.Video, Order = 0 },
                new() { Name = "音频 1", Kind = TrackKind.Audio, Order = 1 }
            }
        },

        new ProjectTemplate
        {
            Id = "hd_1080p_60fps",
            Name = "高清 1080p (60fps)",
            Description = "高帧率视频，适合游戏录制",
            Category = "16:9",
            Width = 1920, Height = 1080, FramesPerSecond = 60,
            Tracks = new()
            {
                new() { Name = "视频 1", Kind = TrackKind.Video, Order = 0 },
                new() { Name = "音频 1", Kind = TrackKind.Audio, Order = 1 }
            }
        },

        new ProjectTemplate
        {
            Id = "4k_2160p_30fps",
            Name = "4K 超清 (30fps)",
            Description = "超高清视频，适合专业制作",
            Category = "16:9",
            Width = 3840, Height = 2160, FramesPerSecond = 30,
            Tracks = new()
            {
                new() { Name = "视频 1", Kind = TrackKind.Video, Order = 0 },
                new() { Name = "音频 1", Kind = TrackKind.Audio, Order = 1 }
            }
        },

        // Vertical Video Templates
        new ProjectTemplate
        {
            Id = "vertical_1080x1920",
            Name = "竖屏 1080x1920",
            Description = "抖音、快手竖屏视频",
            Category = "9:16",
            Width = 1080, Height = 1920, FramesPerSecond = 30,
            Tracks = new()
            {
                new() { Name = "视频 1", Kind = TrackKind.Video, Order = 0 },
                new() { Name = "视频 2", Kind = TrackKind.Video, Order = 1 },
                new() { Name = "音频 1", Kind = TrackKind.Audio, Order = 2 }
            }
        },

        // Square Video Templates
        new ProjectTemplate
        {
            Id = "square_1080x1080",
            Name = "方形 1080x1080",
            Description = "Instagram、微博方形视频",
            Category = "1:1",
            Width = 1080, Height = 1080, FramesPerSecond = 30,
            Tracks = new()
            {
                new() { Name = "视频 1", Kind = TrackKind.Video, Order = 0 },
                new() { Name = "视频 2", Kind = TrackKind.Video, Order = 1 },
                new() { Name = "音频 1", Kind = TrackKind.Audio, Order = 2 }
            }
        },

        // Cinema Templates
        new ProjectTemplate
        {
            Id = "cinema_2k_24fps",
            Name = "电影 2K (24fps)",
            Description = "电影标准帧率，2048x1080",
            Category = "电影",
            Width = 2048, Height = 1080, FramesPerSecond = 24,
            Tracks = new()
            {
                new() { Name = "视频 1", Kind = TrackKind.Video, Order = 0 },
                new() { Name = "视频 2", Kind = TrackKind.Video, Order = 1 },
                new() { Name = "音频 1", Kind = TrackKind.Audio, Order = 2 },
                new() { Name = "音频 2", Kind = TrackKind.Audio, Order = 3 }
            }
        },

        // Multi-track Templates
        new ProjectTemplate
        {
            Id = "multitrack_professional",
            Name = "多轨专业版",
            Description = "3 视频轨 + 4 音频轨，适合复杂剪辑",
            Category = "专业",
            Width = 1920, Height = 1080, FramesPerSecond = 30,
            Tracks = new()
            {
                new() { Name = "视频 3", Kind = TrackKind.Video, Order = 0 },
                new() { Name = "视频 2", Kind = TrackKind.Video, Order = 1 },
                new() { Name = "视频 1", Kind = TrackKind.Video, Order = 2 },
                new() { Name = "对话", Kind = TrackKind.Audio, Order = 3 },
                new() { Name = "音乐", Kind = TrackKind.Audio, Order = 4 },
                new() { Name = "音效", Kind = TrackKind.Audio, Order = 5 },
                new() { Name = "环境音", Kind = TrackKind.Audio, Order = 6 }
            }
        }
    };

    /// <summary>
    /// Gets all available templates (builtin + custom).
    /// </summary>
    public static List<ProjectTemplate> GetAllTemplates()
    {
        var templates = new List<ProjectTemplate>(_builtinTemplates);

        // Load custom templates from user directory
        string customDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TANGERINE-VideoEditor", "Templates");

        if (Directory.Exists(customDir))
        {
            foreach (var file in Directory.GetFiles(customDir, "*.template.json"))
            {
                try
                {
                    var template = LoadTemplateFromFile(file);
                    if (template != null)
                        templates.Add(template);
                }
                catch { /* Skip invalid templates */ }
            }
        }

        return templates;
    }

    /// <summary>
    /// Creates a new project from a template.
    /// </summary>
    public static ProjectDocument CreateFromTemplate(ProjectTemplate template, string projectName)
    {
        var project = new ProjectDocument
        {
            Name = projectName,
            Sequence = new ProjectSequence
            {
                Name = projectName,
                Width = template.Width,
                Height = template.Height,
                FramesPerSecond = template.FramesPerSecond,
                AudioSampleRate = template.AudioSampleRate
            }
        };

        // Create tracks from template
        foreach (var trackTemplate in template.Tracks)
        {
            project.Tracks.Add(new TimelineTrack
            {
                Name = trackTemplate.Name,
                Kind = trackTemplate.Kind,
                Order = trackTemplate.Order
            });
        }

        return project;
    }

    /// <summary>
    /// Saves current project as a template.
    /// </summary>
    public static async Task SaveAsTemplateAsync(ProjectDocument project, string templateName, string category)
    {
        var template = new ProjectTemplate
        {
            Name = templateName,
            Category = category,
            Width = project.Sequence.Width,
            Height = project.Sequence.Height,
            FramesPerSecond = project.Sequence.FramesPerSecond,
            AudioSampleRate = project.Sequence.AudioSampleRate
        };

        // Save track configuration (without clips)
        foreach (var track in project.Tracks)
        {
            template.Tracks.Add(new TrackTemplate
            {
                Name = track.Name,
                Kind = track.Kind,
                Order = track.Order
            });
        }

        // Save to user templates directory
        string customDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TANGERINE-VideoEditor", "Templates");

        Directory.CreateDirectory(customDir);

        string filename = $"{SanitizeFilename(templateName)}.template.json";
        string path = Path.Combine(customDir, filename);

        string json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Gets templates grouped by category.
    /// </summary>
    public static Dictionary<string, List<ProjectTemplate>> GetTemplatesByCategory()
    {
        return GetAllTemplates()
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private static ProjectTemplate? LoadTemplateFromFile(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectTemplate>(json);
    }

    private static string SanitizeFilename(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
