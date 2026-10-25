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
    public NativeVideoView()
    {
        Focusable = true; ClipToBounds = true;
        ContextRequested += (_, e) => { if (RemoteControlEnabled) e.Handled = true; };
    }
    private WriteableBitmap? bitmap;
    private bool frameDirty = true;
    static NativeVideoView() => FrameProperty.Changed.AddClassHandler<NativeVideoView>((view, _) => view.UpdateFrame());
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
    private void Send(int kind, double x = 0, double y = 0, int code = 0, bool down = false, bool positioned = false)
    {
        if (RemoteControlEnabled && Frame != null) InputCommand?.Execute(new RemoteInput(Frame.Identity, kind, x, y, code, down,positioned));
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
        if (!RemoteControlEnabled || Environment.TickCount64 - lastMove < 8 || !Position(e.GetPosition(this), out var x, out var y)) return;
        lastMove = Environment.TickCount64; Send(0, x, y); e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!RemoteControlEnabled || !Position(e.GetPosition(this), out var x, out var y)) return;
        Focus(); e.Pointer.Capture(this); Send(0, x, y);
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        Send(1, x,y,code: kind == PointerUpdateKind.LeftButtonPressed ? 1 : kind == PointerUpdateKind.RightButtonPressed ? 2 : 3, down: true,positioned:true); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); if (!RemoteControlEnabled) return;
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        var positioned=Position(e.GetPosition(this),out var x,out var y);
        Send(1,positioned?x:0,positioned?y:0,code: kind == PointerUpdateKind.LeftButtonReleased ? 1 : kind == PointerUpdateKind.RightButtonReleased ? 2 : 3, down: false,positioned:positioned);
        releasingPointer=true;try{e.Pointer.Capture(null);}finally{releasingPointer=false;}e.Handled = true;
    }
    protected override void OnLostFocus(FocusChangedEventArgs e) { Send(3); base.OnLostFocus(e); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { if(!releasingPointer)Send(3); base.OnPointerCaptureLost(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (RemoteControlEnabled) { if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key is Key.C or Key.V) Send(e.Key == Key.C ? 4 : 5); else if (e.Key == Key.Escape) Send(3); else SendKey(e, true); e.Handled = true; } base.OnKeyDown(e); }
    internal void ForwardKey(int code, bool down, bool ctrl)
    {
        if (ctrl && code is 6 or 25) { if (down) Send(code == 6 ? 4 : 5); return; }
        Send(2, code: code, down: down);
    }
    protected override void OnKeyUp(KeyEventArgs e) { if (RemoteControlEnabled) { SendKey(e, false); e.Handled = true; } base.OnKeyUp(e); }
    private void SendKey(KeyEventArgs e, bool down)
    {
        var code = e.Key is >= Key.A and <= Key.Z ? 4 + (int)e.Key - (int)Key.A
            : e.Key is >= Key.D1 and <= Key.D9 ? 30 + (int)e.Key - (int)Key.D1
            : e.Key is >= Key.F1 and <= Key.F12 ? 58 + (int)e.Key - (int)Key.F1
            : e.Key switch { Key.D0 => 39, Key.Enter => 40, Key.Back => 42, Key.Tab => 43, Key.Space => 44,
                Key.OemMinus => 45, Key.OemPlus => 46, Key.OemOpenBrackets => 47, Key.OemCloseBrackets => 48,
                Key.OemPipe => 49, Key.OemSemicolon => 51, Key.OemQuotes => 52, Key.OemTilde => 53,
                Key.OemComma => 54, Key.OemPeriod => 55, Key.OemQuestion => 56,
                Key.Delete => 76, Key.Right => 79, Key.Left => 80, Key.Down => 81, Key.Up => 82,
                Key.LeftCtrl => 224, Key.LeftShift => 225, Key.LeftAlt => 226, Key.LWin => 227,
                Key.RightCtrl => 228, Key.RightShift => 229, Key.RightAlt => 230, Key.RWin => 231, _ => 0 };
        if (code != 0) Send(2, code: (int)code, down: down);
    }
}
