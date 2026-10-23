using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UserIdentityDesktop.Views;

/// <summary>Native photo navigation. The gallery owns and disposes the authenticated bitmap.</summary>
public sealed class PhotoPreview : UserControl
{
    private readonly Bitmap bitmap;
    private readonly Image picture;
    private readonly Canvas rotated;
    private readonly ScrollViewer viewport;
    private readonly TextBlock percentage = new() { Foreground=Brushes.White, VerticalAlignment=VerticalAlignment.Center, MinWidth=48, TextAlignment=TextAlignment.Center };
    private bool fitting=true;
    private Point? anchor;
    private Vector originalOffset;
    internal double Zoom { get; private set; } = 1;
    internal int Rotation { get; private set; }
    public event Action? FullscreenRequested;
    public PhotoPreview(Bitmap bitmap)
    {
        this.bitmap=bitmap;
        picture=new Image { Source=bitmap, Stretch=Stretch.Fill };
        rotated=new Canvas();rotated.Children.Add(picture);picture.RenderTransformOrigin=RelativePoint.Center;
        viewport=new ScrollViewer { Content=rotated, HorizontalScrollBarVisibility=ScrollBarVisibility.Auto, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalContentAlignment=HorizontalAlignment.Center, VerticalContentAlignment=VerticalAlignment.Center, Background=Brushes.Black };
        var controls=new WrapPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Center, Margin=new Thickness(12) };
        Button Action(string label,Action action)
        {
            var button=new Button {Content=label,Foreground=Brushes.White,Background=new SolidColorBrush(Color.Parse("#252525")),Classes={"quiet"}};
            Avalonia.Automation.AutomationProperties.SetName(button,label);
            button.Click+=(_,_)=>action();controls.Children.Add(button);return button;
        }
        Action("Zoom out",()=>SetZoom(Zoom/1.25));controls.Children.Add(percentage);Action("Zoom in",()=>SetZoom(Zoom*1.25));
        Action("Fit",Fit);Action("Actual size",()=>SetZoom(1));Action("Rotate left",()=>Rotate(-90));Action("Rotate right",()=>Rotate(90));Action("Fullscreen",()=>FullscreenRequested?.Invoke());
        var root=new Grid { RowDefinitions=new RowDefinitions("*,Auto"), Background=Brushes.Black };root.Children.Add(viewport);Grid.SetRow(controls,1);root.Children.Add(controls);Content=root;
        viewport.SizeChanged+=(_,_)=>{if(fitting)Fit();};AttachedToVisualTree+=(_,_)=>Fit();
        viewport.PointerWheelChanged+=(_,e)=>{if(e.KeyModifiers.HasFlag(KeyModifiers.Control)){SetZoom(Zoom*(e.Delta.Y>0?1.25:0.8));e.Handled=true;}};
        viewport.PointerPressed+=(_,e)=>{if(e.GetCurrentPoint(viewport).Properties.IsLeftButtonPressed){anchor=e.GetPosition(viewport);originalOffset=viewport.Offset;e.Pointer.Capture(viewport);}};
        viewport.PointerMoved+=(_,e)=>{if(anchor is {} start){var now=e.GetPosition(viewport);viewport.Offset=new Vector(Math.Max(0,originalOffset.X+start.X-now.X),Math.Max(0,originalOffset.Y+start.Y-now.Y));}};
        viewport.PointerReleased+=(_,e)=>{anchor=null;e.Pointer.Capture(null);};viewport.PointerCaptureLost+=(_,_)=>anchor=null;
    }
    internal void SetZoom(double value)
    {
        fitting=false;Zoom=Math.Clamp(value,.01,16);Apply();
    }
    internal void Fit()
    {
        fitting=true;
        var swapped=Rotation%180!=0;var width=swapped?bitmap.PixelSize.Height:bitmap.PixelSize.Width;var height=swapped?bitmap.PixelSize.Width:bitmap.PixelSize.Height;
        if(viewport.Bounds.Width<=0||viewport.Bounds.Height<=0)return;
        Zoom=Math.Max(.01,Math.Min((viewport.Bounds.Width-24)/width,(viewport.Bounds.Height-24)/height));Apply();viewport.Offset=default;
    }
    internal void Rotate(int angle)
    {
        Rotation=((Rotation+angle)%360+360)%360;picture.RenderTransform=new RotateTransform(Rotation);if(fitting)Fit();else Apply();
    }
    private void Apply()
    {
        picture.Width=bitmap.PixelSize.Width*Zoom;picture.Height=bitmap.PixelSize.Height*Zoom;
        var swapped=Rotation%180!=0;rotated.Width=swapped?picture.Height:picture.Width;rotated.Height=swapped?picture.Width:picture.Height;
        Canvas.SetLeft(picture,(rotated.Width-picture.Width)/2);Canvas.SetTop(picture,(rotated.Height-picture.Height)/2);
        percentage.Text=$"{Zoom*100:0}%";
    }
}
