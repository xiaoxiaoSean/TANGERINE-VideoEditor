using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// AI-powered media search using local models for visual and audio semantic indexing.
/// Enables searching by scene content, spoken words, similar visuals, and audio characteristics.
/// </summary>
public class MediaSemanticSearchService
{
    public enum SearchMode
    {
        Visual,         // Search by visual content (objects, scenes, colors)
        Audio,          // Search by audio content (speech, music, sound effects)
        Transcript,     // Search by spoken words (requires transcription)
        Similarity      // Find visually similar clips
    }

    public sealed class SearchResult
    {
        public MediaAsset Asset { get; set; } = null!;
        public double Score { get; set; }
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public string? MatchedText { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    public sealed class MediaIndex
    {
        public string AssetId { get; set; } = string.Empty;
        public List<VisualFeature> VisualFeatures { get; set; } = new();
        public List<AudioFeature> AudioFeatures { get; set; } = new();
        public List<TranscriptSegment> Transcripts { get; set; } = new();
        public DateTime IndexedTime { get; set; }
    }

    public sealed class VisualFeature
    {
        public double TimeSeconds { get; set; }
        public List<string> Objects { get; set; } = new(); // Detected objects
        public List<string> Scenes { get; set; } = new();  // Scene types (indoor, outdoor, etc)
        public string DominantColor { get; set; } = "#000000";
        public float[] Embedding { get; set; } = Array.Empty<float>(); // Visual embedding vector
    }

    public sealed class AudioFeature
    {
        public double TimeSeconds { get; set; }
        public string Type { get; set; } = "unknown"; // speech, music, silence, noise
        public double Loudness { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    public sealed class TranscriptSegment
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private readonly Dictionary<string, MediaIndex> _indexCache = new();
    private readonly string _indexDirectory;

    public MediaSemanticSearchService(string indexDirectory)
    {
        _indexDirectory = indexDirectory;
        Directory.CreateDirectory(_indexDirectory);
    }

    /// <summary>
    /// Indexes a media asset for semantic search.
    /// </summary>
    public async Task<MediaIndex> IndexMediaAsync(
        MediaAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var index = new MediaIndex
        {
            AssetId = asset.Id,
            IndexedTime = DateTime.Now
        };

        // Step 1: Extract and analyze video frames (if video)
        object? hasVideoObj = asset.Metadata.GetValueOrDefault("HasVideo");
        if (hasVideoObj is bool hasVideo && hasVideo)
        {
            progress?.Report(0.3);
            index.VisualFeatures = await ExtractVisualFeaturesAsync(asset.SourcePath ?? "", cancellationToken);
        }

        // Step 2: Analyze audio (if audio or video with audio)
        object? hasAudioObj = asset.Metadata.GetValueOrDefault("HasAudio");
        if (hasAudioObj is bool hasAudio && hasAudio)
        {
            progress?.Report(0.6);
            index.AudioFeatures = await ExtractAudioFeaturesAsync(asset.SourcePath ?? "", cancellationToken);
        }

        // Step 3: Generate transcript (if speech detected)
        bool hasSpeech = index.AudioFeatures.Any(f => f.Type == "speech");
        if (hasSpeech)
        {
            progress?.Report(0.9);
            index.Transcripts = await GenerateTranscriptAsync(asset.SourcePath ?? "", cancellationToken);
        }

        // Cache and save index
        _indexCache[asset.Id] = index;
        await SaveIndexAsync(index);

        progress?.Report(1.0);
        return index;
    }

    /// <summary>
    /// Searches indexed media by query.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(
        string query,
        SearchMode mode,
        ProjectDocument project,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();

        foreach (var asset in project.Media)
        {
            var index = await LoadIndexAsync(asset.Id);
            if (index == null) continue;

            switch (mode)
            {
                case SearchMode.Transcript:
                    results.AddRange(SearchTranscript(query, asset, index));
                    break;

                case SearchMode.Visual:
                    results.AddRange(SearchVisual(query, asset, index));
                    break;

                case SearchMode.Audio:
                    results.AddRange(SearchAudio(query, asset, index));
                    break;
            }
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// Finds visually similar clips to a reference frame.
    /// </summary>
    public async Task<List<SearchResult>> FindSimilarAsync(
        MediaAsset referenceAsset,
        double referenceTime,
        ProjectDocument project,
        CancellationToken cancellationToken = default)
    {
        var referenceIndex = await LoadIndexAsync(referenceAsset.Id);
        if (referenceIndex == null) return new List<SearchResult>();

        var referenceFeature = referenceIndex.VisualFeatures
            .OrderBy(f => Math.Abs(f.TimeSeconds - referenceTime))
            .FirstOrDefault();

        if (referenceFeature?.Embedding == null || referenceFeature.Embedding.Length == 0)
            return new List<SearchResult>();

        var results = new List<SearchResult>();

        foreach (var asset in project.Media.Where(a => a.Id != referenceAsset.Id))
        {
            var index = await LoadIndexAsync(asset.Id);
            if (index == null) continue;

            foreach (var feature in index.VisualFeatures)
            {
                if (feature.Embedding == null || feature.Embedding.Length == 0) continue;

                double similarity = ComputeCosineSimilarity(referenceFeature.Embedding, feature.Embedding);
                if (similarity > 0.7) // Threshold for similarity
                {
                    results.Add(new SearchResult
                    {
                        Asset = asset,
                        Score = similarity,
                        StartSeconds = feature.TimeSeconds,
                        EndSeconds = feature.TimeSeconds + 1,
                        Tags = feature.Objects
                    });
                }
            }
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    private List<SearchResult> SearchTranscript(string query, MediaAsset asset, MediaIndex index)
    {
        var results = new List<SearchResult>();
        query = query.ToLowerInvariant();

        foreach (var segment in index.Transcripts)
        {
            if (segment.Text.ToLowerInvariant().Contains(query))
            {
                results.Add(new SearchResult
                {
                    Asset = asset,
                    Score = 1.0,
                    StartSeconds = segment.StartSeconds,
                    EndSeconds = segment.EndSeconds,
                    MatchedText = segment.Text
                });
            }
        }

        return results;
    }

    private List<SearchResult> SearchVisual(string query, MediaAsset asset, MediaIndex index)
    {
        var results = new List<SearchResult>();
        query = query.ToLowerInvariant();

        foreach (var feature in index.VisualFeatures)
        {
            bool matchesObjects = feature.Objects.Any(obj => obj.ToLowerInvariant().Contains(query));
            bool matchesScenes = feature.Scenes.Any(scene => scene.ToLowerInvariant().Contains(query));

            if (matchesObjects || matchesScenes)
            {
                results.Add(new SearchResult
                {
                    Asset = asset,
                    Score = 0.9,
                    StartSeconds = feature.TimeSeconds,
                    EndSeconds = feature.TimeSeconds + 1,
                    Tags = feature.Objects.Concat(feature.Scenes).ToList()
                });
            }
        }

        return results;
    }

    private List<SearchResult> SearchAudio(string query, MediaAsset asset, MediaIndex index)
    {
        var results = new List<SearchResult>();
        query = query.ToLowerInvariant();

        foreach (var feature in index.AudioFeatures)
        {
            bool matches = feature.Type.Contains(query) || feature.Tags.Any(tag => tag.ToLowerInvariant().Contains(query));

            if (matches)
            {
                results.Add(new SearchResult
                {
                    Asset = asset,
                    Score = 0.8,
                    StartSeconds = feature.TimeSeconds,
                    EndSeconds = feature.TimeSeconds + 1,
                    Tags = new List<string> { feature.Type }
                });
            }
        }

        return results;
    }

    private async Task<List<VisualFeature>> ExtractVisualFeaturesAsync(string mediaPath, CancellationToken cancellationToken)
    {
        // Placeholder: Real implementation would use a vision model (CLIP, ResNet, etc)
        // Extract frames and analyze with computer vision
        await Task.Delay(100, cancellationToken);

        return new List<VisualFeature>
        {
            new() { TimeSeconds = 0, Objects = new() { "person", "indoor" }, Scenes = new() { "室内" } }
        };
    }

    private async Task<List<AudioFeature>> ExtractAudioFeaturesAsync(string mediaPath, CancellationToken cancellationToken)
    {
        // Placeholder: Real implementation would analyze audio spectrum and classify
        await Task.Delay(100, cancellationToken);

        return new List<AudioFeature>
        {
            new() { TimeSeconds = 0, Type = "speech", Loudness = -20 }
        };
    }

    private async Task<List<TranscriptSegment>> GenerateTranscriptAsync(string mediaPath, CancellationToken cancellationToken)
    {
        // Placeholder: Use SpeechToTextService for actual transcription
        await Task.Delay(100, cancellationToken);

        return new List<TranscriptSegment>();
    }

    private double ComputeCosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private async Task SaveIndexAsync(MediaIndex index)
    {
        string path = Path.Combine(_indexDirectory, $"{index.AssetId}.index.json");
        string json = System.Text.Json.JsonSerializer.Serialize(index);
        await File.WriteAllTextAsync(path, json);
    }

    private async Task<MediaIndex?> LoadIndexAsync(string assetId)
    {
        if (_indexCache.TryGetValue(assetId, out var cached))
            return cached;

        string path = Path.Combine(_indexDirectory, $"{assetId}.index.json");
        if (!File.Exists(path)) return null;

        try
        {
            string json = await File.ReadAllTextAsync(path);
            var index = System.Text.Json.JsonSerializer.Deserialize<MediaIndex>(json);
            if (index != null)
                _indexCache[assetId] = index;
            return index;
        }
        catch
        {
            return null;
        }
    }
}
