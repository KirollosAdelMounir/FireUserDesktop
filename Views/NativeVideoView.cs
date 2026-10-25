using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Input;
using System.Windows.Input;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Views;

/// <summary>Reuses the native bitmap; frame delivery is coalesced before reaching the UI.</summary>
public sealed class NativeVideoView : Control
{
    public static readonly StyledProperty<VideoPicture?> FrameProperty = AvaloniaProperty.Register<NativeVideoView, VideoPicture?>(nameof(Frame));
    public VideoPicture? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public static readonly StyledProperty<bool> RemoteControlEnabledProperty = AvaloniaProperty.Register<NativeVideoView, bool>(nameof(RemoteControlEnabled));
    public bool RemoteControlEnabled { get => GetValue(RemoteControlEnabledProperty); set => SetValue(RemoteControlEnabledProperty, value); }
    public static readonly StyledProperty<ICommand?> InputCommandProperty = AvaloniaProperty.Register<NativeVideoView, ICommand?>(nameof(InputCommand));
    public ICommand? InputCommand { get => GetValue(InputCommandProperty); set => SetValue(InputCommandProperty, value); }
    private long lastMove;
    private bool releasingPointer;
    private bool inputSuspended;
    private Point? lastLinuxPosition;
    // Stop locally before the asynchronous revoke can wait on transport or OS input.
    internal void SuspendRemoteInput() => inputSuspended = true;
    public NativeVideoView()
    {
        Focusable = true; ClipToBounds = true;
        ContextRequested += (_, e) => { if (RemoteControlEnabled) e.Handled = true; };
    }
    private WriteableBitmap? bitmap;
    private bool frameDirty = true;
    static NativeVideoView()
    {
        FrameProperty.Changed.AddClassHandler<NativeVideoView>((view, _) => view.UpdateFrame());
        RemoteControlEnabledProperty.Changed.AddClassHandler<NativeVideoView>((view, _) =>
        { if (!view.RemoteControlEnabled) { view.inputSuspended=false; view.lastLinuxPosition=null; } });
    }
    private void UpdateFrame() { frameDirty = true; InvalidateVisual(); }
    private void CopyFrame()
    {
        var frame = Frame;
        if (frame == null) { bitmap?.Dispose(); bitmap = null; return; }
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > 4096 || frame.Height > 4096 || frame.Rgba.Length != frame.Width * frame.Height * 4) return;
        if (bitmap?.PixelSize != new PixelSize(frame.Width, frame.Height))
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        }
        using (var target = bitmap.Lock())
        {
            if (target.RowBytes == frame.Width * 4) Marshal.Copy(frame.Rgba, 0, target.Address, frame.Rgba.Length);
            else for (var row = 0; row < frame.Height; row++)
                    Marshal.Copy(frame.Rgba, row * frame.Width * 4, target.Address + row * target.RowBytes, frame.Width * 4);
        }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (frameDirty) { frameDirty = false; CopyFrame(); }
        if (bitmap == null) return;
        var scale = Math.Min(Bounds.Width / bitmap.Size.Width, Bounds.Height / bitmap.Size.Height);
        var size = bitmap.Size * scale;
        context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height));
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        bitmap?.Dispose(); bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); UpdateFrame(); }
    private void Send(int kind, double x = 0, double y = 0, int code = 0, bool down = false, bool positioned = false, double dx=0,double dy=0)
    {
        if (RemoteControlEnabled && !inputSuspended && Frame != null) InputCommand?.Execute(new RemoteInput(Frame.Identity, kind, x, y, code, down,positioned,dx,dy));
    }
    private bool Position(Point position, out double x, out double y)
    {
        x = y = 0; if (bitmap == null) return false;
        var scale = Math.Min(Bounds.Width / bitmap.Size.Width, Bounds.Height / bitmap.Size.Height);
        var width = bitmap.Size.Width * scale; var height = bitmap.Size.Height * scale;
        x = (position.X - (Bounds.Width - width) / 2) / width; y = (position.Y - (Bounds.Height - height) / 2) / height;
        return x is >= 0 and <= 1 && y is >= 0 and <= 1;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!RemoteControlEnabled || inputSuspended || Environment.TickCount64 - lastMove < (OperatingSystem.IsLinux()?16:8) || !Position(e.GetPosition(this), out var x, out var y)) return;
        if (OperatingSystem.IsLinux())
        {
            // VM mouse integration can echo an absolute warp back to the viewer.
            // Re-sending the same source pixel creates work without moving the cursor.
            var position=new Point(Math.Floor(x*Frame!.Width),Math.Floor(y*Frame.Height));
            if(lastLinuxPosition==position)return;
            lastLinuxPosition=position;
        }
        lastMove = Environment.TickCount64; Send(0, x, y); e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!RemoteControlEnabled || !Position(e.GetPosition(this), out var x, out var y)) return;
        Focus(); e.Pointer.Capture(this); Send(0, x, y);
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        Send(1, x,y,code: ButtonCode(kind), down: true,positioned:true); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); if (!RemoteControlEnabled) return;
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        var positioned=Position(e.GetPosition(this),out var x,out var y);
        Send(1,positioned?x:0,positioned?y:0,code: ButtonCode(kind), down: false,positioned:positioned);
        releasingPointer=true;try{e.Pointer.Capture(null);}finally{releasingPointer=false;}e.Handled = true;
    }
    private static int ButtonCode(PointerUpdateKind kind)=>kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased=>1,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased=>2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased=>3,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released=>4,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released=>5,_=>0
    };
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if(RemoteControlEnabled && !inputSuspended && Position(e.GetPosition(this),out var x,out var y))
        {Send(6,x,y,positioned:true,dx:Math.Clamp(e.Delta.X,-10,10),dy:Math.Clamp(e.Delta.Y,-10,10));e.Handled=true;}
        base.OnPointerWheelChanged(e);
    }
    internal void ForwardPointer(PointerEventArgs e)
    {
        if(e is PointerWheelEventArgs wheel)OnPointerWheelChanged(wheel);
        else if(e is PointerPressedEventArgs pressed)OnPointerPressed(pressed);
        else if(e is PointerReleasedEventArgs released)OnPointerReleased(released);
        else OnPointerMoved(e);
        e.Handled=true;
    }
    internal void ForwardKeyEvent(KeyEventArgs e,bool down)
    {
        var clipboard=(e.KeyModifiers & (OperatingSystem.IsMacOS()?KeyModifiers.Meta:KeyModifiers.Control))!=0;
        if(clipboard && e.Key is Key.C or Key.V){if(down)Send(e.Key==Key.C?4:5);}
        else SendKey(e,down);
        e.Handled=true;
    }
    protected override void OnLostFocus(FocusChangedEventArgs e) { Send(3); base.OnLostFocus(e); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { if(!releasingPointer)Send(3); base.OnPointerCaptureLost(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if(RemoteControlEnabled&&!e.Handled)ForwardKeyEvent(e,true);base.OnKeyDown(e); }
    internal void ForwardKey(int code, bool down, bool ctrl)
    {
        if (ctrl && code is 6 or 25) { if (down) Send(code == 6 ? 4 : 5); return; }
        Send(2, code: code, down: down);
    }
    protected override void OnKeyUp(KeyEventArgs e) { if (RemoteControlEnabled&&!e.Handled) ForwardKeyEvent(e,false); base.OnKeyUp(e); }
    private void SendKey(KeyEventArgs e, bool down)
    {
        var code = e.Key is >= Key.A and <= Key.Z ? 4 + (int)e.Key - (int)Key.A
            : e.Key is >= Key.D1 and <= Key.D9 ? 30 + (int)e.Key - (int)Key.D1
            : e.Key is >= Key.F1 and <= Key.F12 ? 58 + (int)e.Key - (int)Key.F1
            : e.Key is >= Key.F13 and <= Key.F24 ? 104 + (int)e.Key - (int)Key.F13
            : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? 89 + (int)e.Key - (int)Key.NumPad1
            : e.Key switch { Key.Escape=>41,Key.CapsLock=>57,Key.PrintScreen=>70,Key.Scroll=>71,Key.Pause=>72,Key.Insert=>73,Key.Home=>74,Key.PageUp=>75,Key.End=>77,Key.PageDown=>78,Key.NumLock=>83,Key.Divide=>84,Key.Multiply=>85,Key.Subtract=>86,Key.Add=>87,Key.NumPad0=>98,Key.Decimal=>99,Key.Apps=>101, Key.D0 => 39, Key.Enter => 40, Key.Back => 42, Key.Tab => 43, Key.Space => 44,
                Key.OemMinus => 45, Key.OemPlus => 46, Key.OemOpenBrackets => 47, Key.OemCloseBrackets => 48,
                Key.OemPipe => 49, Key.OemSemicolon => 51, Key.OemQuotes => 52, Key.OemTilde => 53,
                Key.OemComma => 54, Key.OemPeriod => 55, Key.OemQuestion => 56,
                Key.Delete => 76, Key.Right => 79, Key.Left => 80, Key.Down => 81, Key.Up => 82,
                Key.LeftCtrl => 224, Key.LeftShift => 225, Key.LeftAlt => 226, Key.LWin => 227,
                Key.RightCtrl => 228, Key.RightShift => 229, Key.RightAlt => 230, Key.RWin => 231, _ => 0 };
        if (code != 0) Send(2, code: (int)code, down: down);
    }
}
