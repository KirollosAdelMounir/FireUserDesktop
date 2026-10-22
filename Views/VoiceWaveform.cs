using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UserIdentityDesktop.Views;

/// <summary>Angular's 48 actual-sample peaks: 3–48px high, rounded bars, played progress.</summary>
public sealed class VoiceWaveform : Control
{
    public static readonly StyledProperty<double[]?> PeaksProperty = AvaloniaProperty.Register<VoiceWaveform, double[]?>(nameof(Peaks));
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<VoiceWaveform, double>(nameof(Progress));
    public static readonly StyledProperty<IBrush?> InkProperty = AvaloniaProperty.Register<VoiceWaveform, IBrush?>(nameof(Ink), Brushes.White);
    public double[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public IBrush? Ink { get => GetValue(InkProperty); set => SetValue(InkProperty, value); }
    static VoiceWaveform() => AffectsRender<VoiceWaveform>(PeaksProperty, ProgressProperty, InkProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var step = Bounds.Width / 48; var width = Math.Max(.5, Math.Min(4, step - 1));
        for (var i = 0; i < 48; i++)
        {
            var peak = Peaks is { } values && i < values.Length && double.IsFinite(values[i]) ? Math.Clamp(values[i], 0, 1) : 0;
            var height = Math.Min(Bounds.Height, 3 + peak * 45);
            using var opacity = context.PushOpacity(i / 48d < Progress ? 1 : .5);
            context.DrawRectangle(Ink, null, new Rect(i * step + (step - width) / 2, (Bounds.Height - height) / 2, width, height), 4, 4);
        }
    }
}
