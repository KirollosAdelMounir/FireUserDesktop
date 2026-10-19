using Avalonia.Animation.Easings;

namespace UserIdentityDesktop.Views;

/// <summary>The Angular navigation rail's cubic-bezier(.2,.8,.2,1).</summary>
public sealed class AngularNavigationEasing : Easing
{
    public override double Ease(double progress)
    {
        var low = 0d; var high = 1d;
        for (var i = 0; i < 20; i++)
        {
            var t = (low + high) / 2;
            var x = 3 * (1 - t) * (1 - t) * t * .2 + 3 * (1 - t) * t * t * .2 + t * t * t;
            if (x < progress) low = t; else high = t;
        }
        var value = (low + high) / 2;
        return 3 * (1 - value) * (1 - value) * value * .8 + 3 * (1 - value) * value * value + value * value * value;
    }
}
