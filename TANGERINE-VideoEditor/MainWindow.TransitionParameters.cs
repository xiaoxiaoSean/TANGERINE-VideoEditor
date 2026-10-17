using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Number fields for the transition settings that go beyond type and duration
/// (strength of a blur, flash, zoom, page curl, glitch or film wobble, and the
/// turn count of a spin). Shared by the transition tab and the clip's effect
/// page so both edit the same values.
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
        if (TransitionSettings.UsesStrength(transition.PresetId))
            AddTransitionNumber(host, margin, TransitionSettings.StrengthLabel(transition.PresetId),
                TransitionSettings.Strength(transition) * 100,
                TransitionSettings.MinimumStrength * 100, TransitionSettings.MaximumStrength * 100,
                percent => TransitionSettings.Write(transition, TransitionSettings.StrengthKey, percent / 100), changed);
        if (TransitionSettings.UsesTurns(transition.PresetId))
            AddTransitionNumber(host, margin, "旋转圈数（圈）", TransitionSettings.Turns(transition),
                TransitionSettings.MinimumTurns, TransitionSettings.MaximumTurns,
                turns => TransitionSettings.Write(transition, TransitionSettings.TurnsKey, turns), changed);
    }

    private void AddTransitionNumber(Panel host, Thickness margin, string label, double current,
        double minimum, double maximum, Action<double> assign, Action<string> changed)
    {
        static string Show(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
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
                !double.IsFinite(value) || value < minimum || value > maximum)
            {
                StatusText.Text = $"“{label}”须在 {Show(minimum)} 到 {Show(maximum)} 之间 · VIS-TRANSITION-PARAM";
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
