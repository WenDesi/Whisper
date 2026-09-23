using System.Windows;
using System.Windows.Media.Animation;
using MaterialDesignThemes.Wpf;

namespace WhisperDesk.Themes;

public static class DialogTransitions
{
    public static void UseUniformTiming(DialogHost host)
    {
        host.ApplyTemplate();
        var root = host.Template.FindName("DialogHostRoot", host) as FrameworkElement
            ?? throw new InvalidOperationException("DialogHost template is missing its visual-state root.");
        var states = VisualStateManager.GetVisualStateGroups(root).OfType<VisualStateGroup>()
            .Single(group => group.Name == "PopupStates");
        var duration = TimeSpan.FromMilliseconds(240);
        foreach (var transition in states.Transitions.OfType<VisualTransition>()
                     .Where(item => (item.From == "Open" && item.To == "Closed") ||
                                    (item.From == "Closed" && item.To == "Open")))
        {
            var storyboard = transition.Storyboard?.Clone()
                ?? throw new InvalidOperationException("DialogHost template is missing its transition animation.");
            storyboard.Duration = new Duration(duration);

            // Keep the library's targets and easing, without any initial hold frames.
            foreach (var animation in storyboard.Children)
            {
                animation.BeginTime = TimeSpan.Zero;
                animation.Duration = new Duration(duration);
                if (animation is DoubleAnimationUsingKeyFrames values && values.KeyFrames.Count >= 2)
                {
                    var first = values.KeyFrames[0];
                    var last = values.KeyFrames[^1];
                    values.KeyFrames.Clear();
                    first.KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero);
                    last.KeyTime = KeyTime.FromTimeSpan(duration);
                    values.KeyFrames.Add(first);
                    values.KeyFrames.Add(last);
                }
                else if (animation is BooleanAnimationUsingKeyFrames visibility)
                {
                    foreach (var frame in visibility.KeyFrames.OfType<BooleanKeyFrame>())
                        frame.KeyTime = KeyTime.FromTimeSpan(frame.Value ? TimeSpan.Zero : duration);
                }
            }
            transition.Storyboard = storyboard;
        }
    }
}
