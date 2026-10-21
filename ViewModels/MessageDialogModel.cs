namespace UserIdentityDesktop.ViewModels;
public sealed record MessageDialogModel(string Title, string Subtitle, string Body, string Detail, bool IsConfirmation = false);
