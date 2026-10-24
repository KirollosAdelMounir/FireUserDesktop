using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace UserIdentityDesktop.Views;

public sealed partial class DesktopToastWindow : Window
{
    public string ToastTitle { get; }
    public string Message { get; }
    public bool IsChat { get; }
    public Bitmap? Avatar { get; }
    public bool HasAvatar => Avatar != null;
    public string Initials => string.Concat(ToastTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => x[0])).ToUpperInvariant();
    public string ActionName => ToastTitle + ". " + Message + (IsChat ? ". Open chat" : ". Open notification details");
    public bool HasApprovalActions => !IsChat && ToastTitle is "Vacation approval requested" or "Calendar invitation or update";
    public string ApproveLabel => ToastTitle == "Calendar invitation or update" ? "Accept" : "Approve";
    public string RejectLabel => ToastTitle == "Calendar invitation or update" ? "Decline" : "Reject";
    public event Action<string>? DecisionRequested;
    private void Approve_Click(object? sender, RoutedEventArgs e) => DecisionRequested?.Invoke("Approve");
    private void Reject_Click(object? sender, RoutedEventArgs e) => DecisionRequested?.Invoke("Reject");
    public event Action? OpenRequested;
    public event Action? DismissRequested;
    public DesktopToastWindow() : this("Notification", "", false) { }
    public DesktopToastWindow(string title, string message, bool chat, Bitmap? avatar = null)
    {
        ToastTitle = title; Message = message; IsChat = chat; Avatar = avatar;
        InitializeComponent(); DataContext = this;
    }
    private void Open_Click(object? sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    private void Close_Click(object? sender, RoutedEventArgs e) => DismissRequested?.Invoke();
}
