using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Animation;

namespace BattCtl.App.Views;

// A changed value dips and returns (180ms) instead of snapping. That is the
// "subtle cross-fade" the brief asks for when a background refresh lands.
public static class ValueFade
{
    public static void SetText(Microsoft.UI.Xaml.Controls.TextBlock block, string value)
    {
        if (block.Text == value)
        {
            return;
        }

        block.Text = value;
        FadeIn(block);
    }

    public static void SetRunText(Run run, UIElement host, string value)
    {
        if (run.Text == value)
        {
            return;
        }

        run.Text = value;
        FadeIn(host);
    }

    private static void FadeIn(UIElement element)
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 0.35,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
