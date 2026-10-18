using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Builds every transition-specific numeric setting from the renderer schema.
/// The transition tab and clip effect page therefore edit identical serialized
/// values, with validation and undo support before the preview is refreshed.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Adds the extra fields this transition type supports, if any. Each accepted
    /// value takes its own undo step; <paramref name="changed"/> refreshes the
    /// timeline and preview and reports the change.
    /// </summary>
    private void AddTransitionParameterRows(Panel host, ClipTransition transition, Thickness margin,
        Action<string> changed)
    {
        foreach (var track in _project.Tracks)
        foreach (var incoming in track.Clips)
            if (BoundaryCrossfade.TryGetTransition(track, incoming, _project.Sequence.FramesPerSecond,
                out TimelineClip? prior, out ClipTransition? found, out double duration) && found?.Id == transition.Id && prior is not null)
                host.Children.Add(new TextBlock { Text = TransitionHandles.Describe(prior, _project.Media.FirstOrDefault(m => m.Id == prior.MediaAssetId), duration),
                    TextWrapping = TextWrapping.Wrap, Foreground = Brush("#FFB66D"), Margin = margin });
        foreach (TransitionSettings.Parameter parameter in TransitionSettings.Describe(transition.PresetId))
            AddTransitionNumber(host, margin, parameter.Label,
                TransitionSettings.Value(transition, parameter.Key), parameter.Minimum, parameter.Maximum,
                value => TransitionSettings.Write(transition, parameter.Key,
                    parameter.Integer ? Math.Round(value) : value), changed, parameter.Integer);
    }

    private void AddTransitionNumber(Panel host, Thickness margin, string label, double current,
        double minimum, double maximum, Action<double> assign, Action<string> changed, bool integer = false)
    {
        static string Show(double value) => value.ToString("0.####", CultureInfo.CurrentCulture);
        host.Children.Add(new TextBlock
        {
            Text = label, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#D9DCE3"),
            Margin = new Thickness(margin.Left, 0, margin.Right, 3)
        });
        double accepted = current;
        var box = new TextBox
        {
            Text = Show(current), Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(margin.Left, 0, margin.Right, margin.Bottom)
        };
        void Commit()
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                !double.IsFinite(value) || value < minimum || value > maximum ||
                integer && Math.Abs(value - Math.Round(value)) > .000001)
            {
                StatusText.Text = $"“{label}”须为 {Show(minimum)} 到 {Show(maximum)} 之间的{(integer ? "整数" : "数值")} · VIS-TRANSITION-PARAM";
                box.Text = Show(accepted);
                return;
            }
            if (Math.Abs(value - accepted) < .000001) return;
            accepted = value;
            SaveUndoState();
            assign(value);
            MarkProjectDirty();
            changed("已更新转场参数");
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            Commit();
            args.Handled = true;
        };
        host.Children.Add(box);
    }
}
