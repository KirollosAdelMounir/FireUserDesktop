using System;
using Avalonia;
using Avalonia.Controls;

namespace UserIdentityDesktop.Views;

/// <summary>Monitor-sized normal window presentation, without native fullscreen or input grabs.</summary>
internal sealed class BorderlessPresentation
{
    private readonly Window window;
    private PixelPoint normalPosition,previousPosition;
    private Size normalSize,previousSize;
    private WindowState previousState;
    private WindowDecorations previousDecorations;
    private SizeToContent previousSizeToContent;
    private bool previousCanResize;
    private double previousMinWidth,previousMinHeight,previousMaxWidth,previousMaxHeight;
    internal bool IsActive {get;private set;}
    internal BorderlessPresentation(Window window)
    {
        this.window=window;RememberNormalBounds();
        window.SizeChanged+=(_,_)=>RememberNormalBounds();
        window.PositionChanged+=(_,_)=>RememberNormalBounds();
    }
    private void RememberNormalBounds()
    {
        if(IsActive||window.WindowState!=WindowState.Normal)return;
        normalPosition=window.Position;normalSize=new(window.Width,window.Height);
    }
    internal static Size ClientSize(PixelRect monitor,double scaling)=>new(monitor.Width/scaling,monitor.Height/scaling);
    internal void Enter()
    {
        if(IsActive)return;
        var screen=window.Screens.ScreenFromWindow(window)??window.Screens.Primary;
        previousState=window.WindowState;previousPosition=normalPosition;previousSize=normalSize;
        previousDecorations=window.WindowDecorations;previousCanResize=window.CanResize;previousSizeToContent=window.SizeToContent;
        previousMinWidth=window.MinWidth;previousMinHeight=window.MinHeight;previousMaxWidth=window.MaxWidth;previousMaxHeight=window.MaxHeight;
        IsActive=true;
        window.WindowState=WindowState.Normal;window.WindowDecorations=WindowDecorations.None;
        window.SizeToContent=SizeToContent.Manual;
        window.MinWidth=window.MinHeight=0;window.MaxWidth=window.MaxHeight=double.PositiveInfinity;
        if(screen!=null)
        {
            var size=ClientSize(screen.Bounds,screen.Scaling);
            window.Width=size.Width;window.Height=size.Height;window.Position=screen.Bounds.Position;
        }
        // Apply fixed-size hints after requesting the monitor dimensions.
        window.CanResize=false;
    }
    internal void Exit(bool restoreWindowState=true)
    {
        if(!IsActive)return;
        // Keep geometry tracking paused through restoration, including maximization.
        window.WindowDecorations=previousDecorations;window.CanResize=previousCanResize;
        window.MinWidth=previousMinWidth;window.MinHeight=previousMinHeight;window.MaxWidth=previousMaxWidth;window.MaxHeight=previousMaxHeight;
        window.Width=previousSize.Width;window.Height=previousSize.Height;window.Position=previousPosition;
        window.SizeToContent=previousSizeToContent;
        if(restoreWindowState)window.WindowState=previousState;
        IsActive=false;
    }
}
