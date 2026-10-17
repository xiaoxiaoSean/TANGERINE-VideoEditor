namespace TANGERINE_VideoEditor.Editor;

/// <summary>Maps the user-facing preview device choice to FFmpeg decode flags.</summary>
public static class PreviewHardwareAcceleration
{
    public static IReadOnlyList<string> Arguments(string mode)
    {
        return mode switch
        {
            "CPU 软件渲染" => Array.Empty<string>(),
            "NVIDIA CUDA" => new[] { "-hwaccel", "cuda" },
            "Intel QSV" => new[] { "-hwaccel", "qsv" },
            "AMD AMF" => new[] { "-hwaccel", "d3d11va" },
            _ => new[] { "-hwaccel", "auto" }
        };
    }
}
