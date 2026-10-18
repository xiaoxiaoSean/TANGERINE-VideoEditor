using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// AAF (Advanced Authoring Format) interchange support for Avid Media Composer and DaVinci Resolve.
/// AAF is a binary format; this implementation generates AAF-XML which can be converted using external tools.
/// </summary>
public static class AafInterchange
{
    /// <summary>
    /// Exports project to AAF-XML format (can be converted to binary AAF using aaf2xml tool).
    /// </summary>
    public static async Task ExportAafXmlAsync(ProjectDocument project, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var xml = BuildAafXml(project);
        await File.WriteAllTextAsync(outputPath, xml.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static XDocument BuildAafXml(ProjectDocument project)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("AAF",
                new XElement("Header",
                    new XElement("OperationalPattern", "urn:smpte:ul:060E2B34.04010101.0D010201.01010900"),
                    new XElement("EssenceContainers"),
                    new XElement("Identification",
                        new XElement("CompanyName", "TANGERINE VideoEditor"),
                        new XElement("ProductName", "TANGERINE VideoEditor"),
                        new XElement("ProductVersion", AppVersion.Number),
                        new XElement("Date", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"))
                    )
                ),
                new XElement("MetaDictionary"),
                new XElement("Content",
                    BuildContentMob(project),
                    BuildCompositionMob(project),
                    BuildMasterMobs(project),
                    BuildSourceMobs(project)
                )
            )
        );

        // XElement names carry XML namespaces. Writing an xmlns attribute on
        // an unqualified root tries to redefine its already empty namespace
        // and XmlWriter rejects the document at serialization time.
        XNamespace aaf = "http://www.aafassociation.org/aafx/v1.1/20050628";
        foreach (XElement element in doc.Root!.DescendantsAndSelf())
            element.Name = aaf + element.Name.LocalName;
        return doc;
    }

    private static XElement BuildContentMob(ProjectDocument project)
    {
        return new XElement("ContentStorageMob",
            new XAttribute("uid", GenerateUmid()),
            new XElement("Name", project.Name),
            new XElement("Slots",
                BuildTimelineSlots(project)
            )
        );
    }

    private static XElement BuildCompositionMob(ProjectDocument project)
    {
        var sequence = project.Sequence;

        return new XElement("CompositionMob",
            new XAttribute("uid", GenerateUmid()),
            new XElement("Name", sequence.Name),
            new XElement("Slots",
                BuildVideoSlots(project),
                BuildAudioSlots(project)
            ),
            new XElement("EditRate",
                new XElement("Numerator", (int)sequence.FramesPerSecond),
                new XElement("Denominator", 1)
            ),
            new XElement("Length", (int)(GetSequenceDuration(project) * sequence.FramesPerSecond))
        );
    }

    private static IEnumerable<XElement> BuildTimelineSlots(ProjectDocument project)
    {
        int slotId = 1;

        // Video tracks
        foreach (var track in project.Tracks.Where(t => t.Kind == TrackKind.Video).OrderBy(t => t.Order))
        {
            yield return new XElement("TimelineSlot",
                new XAttribute("slotID", slotId++),
                new XElement("Name", track.Name),
                new XElement("PhysicalTrackNumber", track.Order + 1),
                new XElement("Segment",
                    BuildSequenceForTrack(track, project)
                )
            );
        }

        // Audio tracks
        foreach (var track in project.Tracks.Where(t => t.Kind == TrackKind.Audio).OrderBy(t => t.Order))
        {
            yield return new XElement("TimelineSlot",
                new XAttribute("slotID", slotId++),
                new XElement("Name", track.Name),
                new XElement("PhysicalTrackNumber", track.Order + 1),
                new XElement("Segment",
                    BuildSequenceForTrack(track, project)
                )
            );
        }
    }

    private static XElement BuildSequenceForTrack(TimelineTrack track, ProjectDocument project)
    {
        var sequence = new XElement("Sequence",
            new XElement("ComponentLength", (int)(GetSequenceDuration(project) * project.Sequence.FramesPerSecond)),
            new XElement("Components")
        );

        var sortedClips = track.Clips.OrderBy(c => c.StartSeconds).ToList();
        double currentTime = 0;

        foreach (var clip in sortedClips)
        {
            // Add filler if there's a gap
            if (clip.StartSeconds > currentTime)
            {
                sequence.Element("Components")!.Add(
                    BuildFiller((clip.StartSeconds - currentTime) * project.Sequence.FramesPerSecond)
                );
            }

            // Add source clip
            sequence.Element("Components")!.Add(
                BuildSourceClip(clip, project)
            );

            currentTime = clip.StartSeconds + clip.DurationSeconds;
        }

        return sequence;
    }

    private static XElement BuildSourceClip(TimelineClip clip, ProjectDocument project)
    {
        var asset = project.Media.FirstOrDefault(m => m.Id == clip.MediaAssetId);

        return new XElement("SourceClip",
            new XElement("ComponentLength", (int)(clip.DurationSeconds * project.Sequence.FramesPerSecond)),
            new XElement("SourceID", asset?.Id ?? "missing"),
            new XElement("SourceMobSlotID", 1),
            new XElement("StartTime", (int)(clip.SourceInSeconds * project.Sequence.FramesPerSecond))
        );
    }

    private static XElement BuildFiller(double lengthInFrames)
    {
        return new XElement("Filler",
            new XElement("ComponentLength", (int)lengthInFrames)
        );
    }

    private static XElement BuildVideoSlots(ProjectDocument project)
    {
        return new XElement("VideoSlot",
            new XAttribute("slotID", 1),
            new XElement("Name", "Video 1")
        );
    }

    private static XElement BuildAudioSlots(ProjectDocument project)
    {
        return new XElement("AudioSlot",
            new XAttribute("slotID", 2),
            new XElement("Name", "Audio 1")
        );
    }

    private static XElement BuildMasterMobs(ProjectDocument project)
    {
        var mobs = new XElement("MasterMobs");

        foreach (var asset in project.Media)
        {
            mobs.Add(new XElement("MasterMob",
                new XAttribute("uid", asset.Id),
                new XElement("Name", asset.Name),
                new XElement("Slots",
                    new XElement("TimelineSlot",
                        new XAttribute("slotID", 1),
                        new XElement("SourceClip",
                            new XElement("SourceID", asset.Id)
                        )
                    )
                )
            ));
        }

        return mobs;
    }

    private static XElement BuildSourceMobs(ProjectDocument project)
    {
        var mobs = new XElement("SourceMobs");

        foreach (var asset in project.Media)
        {
            mobs.Add(new XElement("SourceMob",
                new XAttribute("uid", asset.Id),
                new XElement("Name", asset.Name),
                new XElement("EssenceDescriptor",
                    new XElement("FileDescriptor",
                        new XElement("SampleRate",
                            new XElement("Numerator", 30),
                            new XElement("Denominator", 1)
                        ),
                        new XElement("ContainerFormat", "urn:smpte:ul:060E2B34.04010101.0D010301.02060100"),
                        new XElement("Length", 1000),
                        new XElement("Locator",
                            new XElement("URLLocator",
                                new XElement("URL", asset.SourcePath ?? "")
                            )
                        )
                    )
                )
            ));
        }

        return mobs;
    }

    private static double GetSequenceDuration(ProjectDocument project)
    {
        double maxEnd = 0;
        foreach (var track in project.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                maxEnd = Math.Max(maxEnd, clip.StartSeconds + clip.DurationSeconds);
            }
        }
        return maxEnd;
    }

    private static string GenerateUmid()
    {
        return "urn:smpte:umid:" + Guid.NewGuid().ToString("N").Substring(0, 32);
    }
}
