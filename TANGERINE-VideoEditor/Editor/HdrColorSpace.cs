using System;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// HDR (High Dynamic Range) color space definitions and conversion utilities.
/// Supports Rec.2020, PQ (Perceptual Quantizer ST.2084), and HLG (Hybrid Log-Gamma).
/// </summary>
public static class HdrColorSpace
{
    public enum ColorSpace
    {
        Rec709,      // SDR HD (BT.709)
        Rec601,      // SDR SD (BT.601)
        Rec2020,     // HDR/WCG (BT.2020 primaries)
    }

    public enum TransferFunction
    {
        SDR,         // Standard gamma 2.4
        PQ,          // Perceptual Quantizer (ST.2084) for HDR10
        HLG,         // Hybrid Log-Gamma (BT.2100 HLG)
        Log,         // Logarithmic (camera log formats)
    }

    public sealed record HdrConfiguration(
        ColorSpace Primaries,
        TransferFunction Transfer,
        int BitDepth,
        double MaxNits,
        double MinNits
    )
    {
        public bool IsHdr => Transfer is TransferFunction.PQ or TransferFunction.HLG;
        public bool Is10Bit => BitDepth >= 10;
    }

    // Standard configurations
    public static readonly HdrConfiguration Sdr709_8bit = new(
        ColorSpace.Rec709, TransferFunction.SDR, 8, 100, 0.01);

    public static readonly HdrConfiguration Hdr10_10bit = new(
        ColorSpace.Rec2020, TransferFunction.PQ, 10, 10000, 0.0001);

    public static readonly HdrConfiguration Hlg_10bit = new(
        ColorSpace.Rec2020, TransferFunction.HLG, 10, 1000, 0);

    /// <summary>
    /// Gets FFmpeg color space filter arguments for conversion.
    /// </summary>
    public static string GetColorSpaceFilter(HdrConfiguration source, HdrConfiguration target)
    {
        if (source.Primaries == target.Primaries && source.Transfer == target.Transfer)
            return ""; // No conversion needed

        string primariesIn = MapPrimaries(source.Primaries);
        string primariesOut = MapPrimaries(target.Primaries);
        string transferIn = MapTransfer(source.Transfer);
        string transferOut = MapTransfer(target.Transfer);

        return $"colorspace=ispace={primariesIn}:space={primariesOut}:" +
               $"itrc={transferIn}:trc={transferOut}:" +
               $"irange=tv:range=tv";
    }

    /// <summary>
    /// Gets FFmpeg pixel format string for the specified bit depth.
    /// </summary>
    public static string GetPixelFormat(int bitDepth) => bitDepth switch
    {
        8 => "yuv420p",
        10 => "yuv420p10le",
        12 => "yuv420p12le",
        _ => "yuv420p"
    };

    /// <summary>
    /// Applies PQ (Perceptual Quantizer) EOTF for HDR10.
    /// Input: linear light [0, 1], Output: PQ-encoded [0, 1]
    /// </summary>
    public static double ApplyPQ(double linearLight)
    {
        const double m1 = 2610.0 / 16384.0;
        const double m2 = 2523.0 / 4096.0 * 128.0;
        const double c1 = 3424.0 / 4096.0;
        const double c2 = 2413.0 / 4096.0 * 32.0;
        const double c3 = 2392.0 / 4096.0 * 32.0;

        double y = Math.Pow(linearLight, m1);
        return Math.Pow((c1 + c2 * y) / (1 + c3 * y), m2);
    }

    /// <summary>
    /// Applies inverse PQ EOTF (OETF) for HDR10.
    /// Input: PQ-encoded [0, 1], Output: linear light [0, 1]
    /// </summary>
    public static double InversePQ(double pqValue)
    {
        const double m1 = 2610.0 / 16384.0;
        const double m2 = 2523.0 / 4096.0 * 128.0;
        const double c1 = 3424.0 / 4096.0;
        const double c2 = 2413.0 / 4096.0 * 32.0;
        const double c3 = 2392.0 / 4096.0 * 32.0;

        double n = Math.Pow(pqValue, 1.0 / m2);
        return Math.Pow(Math.Max(n - c1, 0) / (c2 - c3 * n), 1.0 / m1);
    }

    /// <summary>
    /// Applies HLG (Hybrid Log-Gamma) OETF.
    /// Input: linear light [0, 1], Output: HLG-encoded [0, 1]
    /// </summary>
    public static double ApplyHLG(double linearLight)
    {
        const double a = 0.17883277;
        const double b = 0.28466892;
        const double c = 0.55991073;

        if (linearLight <= 1.0 / 12.0)
            return Math.Sqrt(3.0 * linearLight);
        else
            return a * Math.Log(12.0 * linearLight - b) + c;
    }

    /// <summary>
    /// Applies inverse HLG OETF (EOTF).
    /// Input: HLG-encoded [0, 1], Output: linear light [0, 1]
    /// </summary>
    public static double InverseHLG(double hlgValue)
    {
        const double a = 0.17883277;
        const double b = 0.28466892;
        const double c = 0.55991073;

        if (hlgValue <= 0.5)
            return (hlgValue * hlgValue) / 3.0;
        else
            return (Math.Exp((hlgValue - c) / a) + b) / 12.0;
    }

    /// <summary>
    /// Converts nits (cd/m²) to normalized linear light for PQ encoding.
    /// </summary>
    public static double NitsToLinear(double nits, double maxNits = 10000.0)
    {
        return Math.Clamp(nits / maxNits, 0.0, 1.0);
    }

    /// <summary>
    /// Converts normalized linear light to nits (cd/m²).
    /// </summary>
    public static double LinearToNits(double linear, double maxNits = 10000.0)
    {
        return linear * maxNits;
    }

    /// <summary>
    /// Tone maps HDR to SDR using Reinhard operator.
    /// </summary>
    public static double ToneMapReinhard(double hdrLinear, double whitePoint = 1.0)
    {
        return hdrLinear / (hdrLinear + whitePoint);
    }

    /// <summary>
    /// Tone maps HDR to SDR using ACES filmic curve.
    /// </summary>
    public static double ToneMapAces(double hdrLinear)
    {
        const double a = 2.51;
        const double b = 0.03;
        const double c = 2.43;
        const double d = 0.59;
        const double e = 0.14;

        return Math.Clamp((hdrLinear * (a * hdrLinear + b)) /
                         (hdrLinear * (c * hdrLinear + d) + e), 0.0, 1.0);
    }

    private static string MapPrimaries(ColorSpace space) => space switch
    {
        ColorSpace.Rec709 => "bt709",
        ColorSpace.Rec601 => "bt601",
        ColorSpace.Rec2020 => "bt2020",
        _ => "bt709"
    };

    private static string MapTransfer(TransferFunction transfer) => transfer switch
    {
        TransferFunction.SDR => "bt709",
        TransferFunction.PQ => "smpte2084",
        TransferFunction.HLG => "arib-std-b67",
        TransferFunction.Log => "log",
        _ => "bt709"
    };

    /// <summary>
    /// Gets FFmpeg metadata tags for HDR10 mastering display and content light.
    /// </summary>
    public static Dictionary<string, string> GetHdr10Metadata(
        double maxNits = 1000,
        double minNits = 0.0001,
        double maxCLL = 1000,
        double maxFALL = 400)
    {
        return new Dictionary<string, string>
        {
            // Mastering display color volume (SMPTE ST 2086)
            ["master_display"] = $"G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)" +
                                $"L({(int)(maxNits * 10000)},{(int)(minNits * 10000)})",
            // Content light level (CEA-861.3)
            ["max_cll"] = $"{(int)maxCLL},{(int)maxFALL}"
        };
    }
}
