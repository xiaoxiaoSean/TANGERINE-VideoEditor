using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// The authoritative transition implementation. Every transition uses the same
/// clip-local clock in the still compositor, catalogue samples and video export.
/// Spatial effects resample RGBA on a fixed canvas, so animated rotation and zoom
/// cannot change filter-link dimensions or stretch transparent borders.
/// </summary>
public static class TransitionRenderPlan
{
    public static string ProgressExpression(ClipTransition transition, double clipDuration, string clock)
    {
        double duration = Math.Min(clipDuration / 2, Math.Max(0, transition.DurationSeconds));
        if (duration <= .000001) return "1";
        string raw = transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase)
            ? $"clip(({N(clipDuration)}-({clock}))/{N(duration)},0,1)"
            : $"clip(({clock})/{N(duration)},0,1)";
        return TransitionSettings.Value(transition, "easing") switch
        {
            1 => $"(({raw})*({raw})*(3-2*({raw})))",
            2 => $"pow({raw},2)",
            3 => $"(1-pow(1-({raw}),2))",
            _ => raw
        };
    }

    /// <summary>
    /// Returns a filter chain, including internal labelled branches for a real
    /// Gaussian blur. clock is T for video or a numeric playhead time for stills;
    /// prefix must be a unique, application-generated filter label.
    /// </summary>
    public static string BuildFilters(ClipTransition transition, double clipDuration, string clock,
        string prefix, int canvasWidth)
    {
        if (!double.IsFinite(transition.DurationSeconds) || transition.DurationSeconds < 0 ||
            !double.IsFinite(clipDuration) || clipDuration <= 0)
            throw new ExportException(ExportStageCode.Validate, "转场时长无效，无法预览或导出。");
        if (transition.DurationSeconds <= .000001) return string.Empty;
        string p = ProgressExpression(transition, clipDuration, clock);
        // Outside its active edge the transition is exactly the identity.
        // Generic timeline support skips the complete per-pixel filter there,
        // rather than evaluating the same identity expression for every plane.
        // Numeric still-preview clocks retain their original evaluation path.
        double activeDuration = Math.Min(clipDuration / 2, transition.DurationSeconds);
        string enable = clock == "T"
            ? $":enable='{E(transition.Edge.Equals("Out", StringComparison.OrdinalIgnoreCase)
                ? $"gt(t,{N(clipDuration - activeDuration)})" : $"lt(t,{N(activeDuration)})")}'"
            : string.Empty;
        string id = transition.PresetId;
        double strength = TransitionSettings.Strength(transition);
        double V(string key) => TransitionSettings.Value(transition, key);
        string centerX = N(V("centerX")), centerY = N(V("centerY"));
        string x = "X", y = "Y", alpha = "1";
        string r = "r(ld(2),ld(3))", g = "g(ld(2),ld(3))", b = "b(ld(2),ld(3))";
        string pre = string.Empty;
        string frameSetup = string.Empty;
        string q = "ld(0)", remaining = "ld(1)";
        string Feather(string distance)
        {
            if (V("reverse") > .5) distance = $"(1-({distance}))";
            double feather = V("feather");
            // Progress endpoints must be exact even with a soft matte. Without
            // these guards, the very first / last frame retains a border halo.
            return $"if(lte({q},0),0,if(gte({q},1),1," + (feather <= .000001
                ? $"lte({distance},{q})" : $"clip(({q}*(1+{N(feather)})-({distance}))/{N(feather)},0,1)") + "))";
        }
        switch (id)
        {
            case "transition.fade": alpha = q; break;
            case "transition.crossfade": alpha = q; break;
            case "transition.dissolve": alpha = $"({q})*({q})*(3-2*({q}))"; break;
            case "transition.wipe-left": alpha = Feather("1-X/W"); break;
            case "transition.wipe-right": alpha = Feather("X/W"); break;
            case "transition.wipe-up": alpha = Feather("1-Y/H"); break;
            case "transition.wipe-down": alpha = Feather("Y/H"); break;
            case "transition.circle":
                alpha = Feather($"hypot(X-W*{centerX},Y-H*{centerY})/hypot(W*max({centerX},1-{centerX}),H*max({centerY},1-{centerY}))"); break;
            case "transition.circle-open":
                // Iris uses normalized elliptical distance; circle preserves
                // physical pixels and therefore remains round on wide canvases.
                alpha = Feather($"hypot((X/W-{centerX})/max({centerX},1-{centerX}),(Y/H-{centerY})/max({centerY},1-{centerY}))/sqrt(2)"); break;
            case "transition.diamond":
                // A Manhattan-distance matte produces four straight edges.
                alpha = Feather($"(abs(X/W-{centerX})/max({centerX},1-{centerX})+abs(Y/H-{centerY})/max({centerY},1-{centerY}))/2"); break;
            case "transition.split": alpha = Feather("abs(2*X/W-1)"); break;
            case "transition.slide-left": x = $"X-{remaining}*W"; break;
            case "transition.slide-right": x = $"X+{remaining}*W"; break;
            case "transition.push-up": y = $"Y-{remaining}*H"; break;
            case "transition.push-down": y = $"Y+{remaining}*H"; break;
            case "transition.zoom-in":
            case "transition.zoom-out":
            case "transition.zoom":
            case "transition.spin":
            case "transition.rotate":
            {
                string zoom = id switch
                {
                    "transition.zoom-in" => $"max(.05,1-{N(.7 * strength)}*{remaining})",
                    "transition.zoom-out" => $"(1+{N(1.5 * strength)}*{remaining})",
                    "transition.zoom" => $"max(.05,1+{N(.4 * strength)}*sin(3*PI*{q})*{remaining})",
                    "transition.spin" => $"(.2+.8*{q})",
                    _ => "1"
                };
                string angle = id == "transition.spin" ? $"{remaining}*{N(2 * Math.PI * V("turns") * (V("direction") < 0 ? -1 : 1))}" :
                    id == "transition.rotate" ? $"{remaining}*{N(V("angle") * Math.PI / 180)}" : "0";
                string dx = $"(X-W*{centerX})", dy = $"(Y-H*{centerY})";
                if (clock == "T")
                {
                    // These values depend on the frame clock, never on X/Y.
                    // Reusing them avoids four trigonometric evaluations per
                    // pixel in every color/alpha plane of a rotating picture.
                    frameSetup = $"st(4,cos({angle}));st(5,sin({angle}));st(6,{zoom});";
                    x = $"W*{centerX}+({dx}*ld(4)+{dy}*ld(5))/ld(6)";
                    y = $"H*{centerY}+(-{dx}*ld(5)+{dy}*ld(4))/ld(6)";
                }
                else
                {
                    x = $"W*{centerX}+({dx}*cos({angle})+{dy}*sin({angle}))/({zoom})";
                    y = $"H*{centerY}+(-{dx}*sin({angle})+{dy}*cos({angle}))/({zoom})";
                }
                alpha = id == "transition.rotate" ? $"sqrt({q})" : q;
                break;
            }
            case "transition.shake":
                string shakeX = $"W*{N(.025 * strength)}*{remaining}*sin(2*PI*{N(V("frequency"))}*({clock}))";
                string shakeY = $"H*{N(.014 * strength)}*{remaining}*sin(2*PI*{N(V("frequency") * 1.37)}*({clock})+.8)";
                if (clock == "T")
                {
                    frameSetup = $"st(4,{shakeX});st(5,{shakeY});";
                    x = "X+ld(4)"; y = "Y+ld(5)";
                }
                else { x = $"X+{shakeX}"; y = $"Y+{shakeY}"; }
                alpha = q; break;
            case "transition.film":
                y = $"mod(Y+H*{N(2 * strength)}*{remaining},H)";
                string gate = $"(1-{N(.28 * strength)}*{remaining}*pow(cos(PI*mod(Y/H*4+{remaining}*8,1)),8))";
                string flicker = $"(1-{N(.15 * strength)}*{remaining}*(.5+.5*sin(2*PI*{N(V("frequency"))}*({clock}))))";
                r = $"({r})*{gate}*{flicker}"; g = $"({g})*{gate}*{flicker}"; b = $"({b})*{gate}*{flicker}";
                alpha = q; break;
            case "transition.glitch":
                string displacement = $"W*{N(.12 * strength)}*{remaining}*sin(floor(Y/H*{N(V("count"))})*17+floor(({clock})*{N(V("frequency"))})*9)";
                x = $"X+{displacement}";
                r = $"r(ld(2)+W*{N(.012 * strength)}*{remaining},ld(3))";
                b = $"b(ld(2)-W*{N(.012 * strength)}*{remaining},ld(3))";
                alpha = q; break;
            case "transition.blur":
            case "transition.blur-dissolve":
            {
                // FFmpeg 4.4 gblur sigma is constant. Mix its real Gaussian
                // output with the sharp source using the same animated weight
                // in both renderers; avoid the former brightness-only fallback.
                double sigma = Math.Clamp(canvasWidth * V("radius") * strength, .1, 1024);
                string weight = id == "transition.blur" ? $"pow(1-({p}),2)" : $"sin(PI*({p}))";
                string blendExpression = $"A*(1-({weight}))+B*({weight})";
                if (clock == "T")
                    blendExpression = $"if(eq(ld(8),0)+not(eq(ld(9),T)),st(0,{weight});st(8,1);st(9,T),0);A*(1-ld(0))+B*ld(0)";
                pre = $",split=2[{prefix}s][{prefix}b];[{prefix}b]gblur=sigma={N(sigma)}{enable}[{prefix}g];" +
                    $"[{prefix}s][{prefix}g]blend=all_expr='{E(blendExpression)}'{enable}";
                alpha = id == "transition.blur" ? q : $"{q}*{q}*(3-2*{q})";
                break;
            }
            case "transition.dip-black":
            case "transition.dip-white":
            case "transition.flash":
            {
                string mix = id == "transition.flash" ? $"min(1,{remaining}*{N(.95 * strength)})" : remaining;
                double target = id == "transition.dip-black" ? 0 : 255;
                r = $"({r})*(1-({mix}))+{N(target)}*({mix})";
                g = $"({g})*(1-({mix}))+{N(target)}*({mix})";
                b = $"({b})*(1-({mix}))+{N(target)}*({mix})";
                break;
            }
            case "transition.light-leak":
            {
                string glow = $"min(1,{remaining}*{N(strength)}*exp(-pow((X/W-({q}*1.3-.15))/{N(V("spread"))},2)))";
                r = $"({r})*(1-({glow}))+255*({glow})";
                g = $"({g})*(1-({glow}))+{N(255 - 90 * V("warmth"))}*({glow})";
                b = $"({b})*(1-({glow}))+{N(255 - 210 * V("warmth"))}*({glow})";
                alpha = $"max({q},({glow}))"; break;
            }
            case "transition.page":
            {
                string edge = $"(X/W+{N(.2 * strength)}*sin(PI*Y/H))/{N(1 + .2 * strength)}";
                alpha = $"if(gte({q},1),1,lte({edge},{q}))";
                string shade = $"(1-.65*{remaining}*exp(-pow((({edge})-{q})/{N(V("fold"))},2)))";
                r = $"({r})*{shade}"; g = $"({g})*{shade}"; b = $"({b})*{shade}"; break;
            }
            case "transition.pixelize":
                string block = $"max(1,W*{N(V("blockSize"))}*{remaining})";
                x = $"clip(if(gte({q},1),X,floor(X/({block}))*({block})+({block})/2),0,W-1)";
                y = $"clip(if(gte({q},1),Y,floor(Y/({block}))*({block})+({block})/2),0,H-1)";
                alpha = q; break;
            case "transition.radial":
                string angleDistance = $"mod({N(V("direction") < 0 ? -1 : 1)}*(atan2(Y-H*{centerY},X-W*{centerX})-{N(V("startAngle") * Math.PI / 180)})+4*PI,2*PI)/(2*PI)";
                alpha = $"if(gte({q},1),1,lte({angleDistance},{q}))"; break;
            case "transition.blinds":
                string blindDistance = $"mod(Y/H*{N(V("count"))},1)";
                if (V("reverse") > .5) blindDistance = $"(1-({blindDistance}))";
                alpha = $"lte({blindDistance},{q})"; break;
            case "transition.checker":
                string rank = $"(mod(floor(X/W*{N(V("count"))})+floor(Y/H*{N(V("count"))}),2)+mod(X/W*{N(V("count"))},1))/2";
                if (V("reverse") > .5) rank = $"(1-({rank}))";
                alpha = $"lte({rank},{q})"; break;
            case "transition.diagonal": alpha = Feather($"(X/W+{N(V("slope"))}*Y/H)/{N(1 + V("slope"))}"); break;
            case "transition.luma": alpha = Feather("(0.2126*r(X,Y)+0.7152*g(X,Y)+0.0722*b(X,Y))/255"); break;
            default: throw new ExportException(ExportStageCode.Validate, $"不支持的转场类型“{id}”，请重新选择转场。");
        }
        // Each plane owns its expression registers. Store the shared progress,
        // inverse progress and inverse-mapped coordinates in every plane before
        // sampling it. The explicit bounds mask prevents FFmpeg edge clamping
        // from smearing a moving image into the exposed transparent area.
        if (id is not ("transition.dip-black" or "transition.dip-white" or "transition.flash"))
            alpha = $"if(lte({q},0),0,if(gte({q},1),1,({alpha})))";
        // AVExpr registers belong to each parsed expression and slice worker;
        // no plane or thread reads another worker's state. Register 8 marks
        // initialization (including T=0), and register 9 tracks that worker's
        // frame clock. Refresh the constants on the first pixel of every new
        // frame, while registers 2/3 remain strictly per-pixel coordinates.
        string frameValues = $"st(0,{p});st(1,1-ld(0));{frameSetup}";
        string setup = (clock == "T"
            ? $"if(eq(ld(8),0)+not(eq(ld(9),T)),{frameValues}st(8,1);st(9,T),0);"
            : frameValues) + $"st(2,{x});st(3,{y});";
        string bounds = "between(ld(2),0,W-1)*between(ld(3),0,H-1)";
        string a = $"alpha(ld(2),ld(3))*({alpha})*({bounds})";
        return pre + $",geq=r='{E(setup + r)}':g='{E(setup + g)}':b='{E(setup + b)}':a='{E(setup + a)}':interpolation=bilinear{enable}";
    }

    /// <summary>
    /// A boundary transition stored on the outgoing clip is evaluated on the
    /// incoming picture too. The outgoing last frame is held by the caller.
    /// This also prevents applying both halves of the same transition twice.
    /// </summary>
    public static IReadOnlyList<ClipTransition> ForClip(TimelineTrack track, TimelineClip clip, double fps)
    {
        if (!BoundaryCrossfade.TryGetTransition(track, clip, fps, out _, out ClipTransition? boundary, out double duration) ||
            boundary is null) return clip.Transitions;
        var effective = new ClipTransition
        {
            PresetId = boundary.PresetId, DurationSeconds = duration,
            Edge = "In", Parameters = boundary.Parameters
        };
        if (clip.Transitions.Any(item => ReferenceEquals(item, boundary)))
            return clip.Transitions.Select(item => ReferenceEquals(item, boundary) ? effective : item).ToArray();
        return clip.Transitions.Concat([effective]).ToArray();
    }

    public static (ClipTransition Transition, double ClipDuration, double Start)? HeldBoundary(
        TimelineTrack track, TimelineClip prior, double fps)
    {
        foreach (TimelineClip incoming in track.Clips)
            if (BoundaryCrossfade.TryGetTransition(track, incoming, fps, out TimelineClip? found,
                out ClipTransition? transition, out double duration) && found?.Id == prior.Id && transition is not null)
                return (new ClipTransition
                {
                    PresetId = transition.PresetId, DurationSeconds = duration,
                    Edge = "In", Parameters = transition.Parameters
                }, incoming.DurationSeconds, incoming.StartSeconds);
        return null;
    }

    /// <summary>
    /// Push is a two-picture motion: move the held outgoing frame in the same
    /// direction as the entering frame. A plain slide intentionally leaves the
    /// outgoing frame in place, which makes these effects visibly distinct.
    /// </summary>
    public static string BuildHeldFilters(ClipTransition transition, double clipDuration, string clock)
    {
        if (transition.PresetId is not ("transition.push-up" or "transition.push-down")) return string.Empty;
        string progress = ProgressExpression(transition, clipDuration, clock);
        string y = transition.PresetId == "transition.push-up" ? $"Y+({progress})*H" : $"Y-({progress})*H";
        return $",geq=r='{E($"r(X,{y})")}':g='{E($"g(X,{y})")}':b='{E($"b(X,{y})")}':" +
            $"a='{E($"alpha(X,{y})*between({y},0,H-1)")}'";
    }
    private static string N(double value) => VisualEffectPipeline.Number(value);
    private static string E(string value) => VisualEffectPipeline.EscapeExpression(value);
}
