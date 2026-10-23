using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

internal static class MessageMenus
{
    internal static ContextMenu Create(ChatViewModel model, ChatMessageItemViewModel item, Control target)
    {
        var menu = new ContextMenu();
        MenuItem Action(string label, Action action) => new() { Header = label, Command = new RelayCommand(action) };
        MenuItem Async(string label, Func<Task> action) => new() { Header = label, Command = new AsyncRelayCommand(async () => { try { await action(); } catch (Exception ex) { model.SendError = ex.Message; } }) };
        if (!item.IsCall && item.Message.IsDeleted != true)
        {
            var reactions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            foreach (var (emoji, label) in new[] { ("👍", "Like"), ("❤️", "Love"), ("😂", "Laugh"), ("😢", "Sad"), ("😡", "Angry") })
            {
                var reaction = new Button { Content = emoji, FontSize = 22, FontFamily = new Avalonia.Media.FontFamily("Noto Color Emoji, Segoe UI Emoji, Apple Color Emoji"), Padding = new Avalonia.Thickness(7), Classes = { "quiet" } };
                Avalonia.Automation.AutomationProperties.SetName(reaction, label);
                reaction.Click += async (_, _) => { menu.Close(); try { await model.React(item, emoji); } catch (Exception error) { model.SendError = error.Message; } };
                reactions.Children.Add(reaction);
            }
            menu.Items.Add(new MenuItem { Header = reactions, StaysOpenOnClick = true });
            menu.Items.Add(new Separator());
            menu.Items.Add(Action("Reply", () => model.BeginReply(item)));
            if (item.HasText) menu.Items.Add(Async("Copy text", async () => { if (TopLevel.GetTopLevel(target)?.Clipboard is {} clipboard) await clipboard.SetTextAsync(item.Text); }));
            if (item.CanEdit) menu.Items.Add(Action("Edit message", () => model.BeginEdit(item)));

        }
        else if (item.CanJoinCall) menu.Items.Add(Async("Join call", async () => { if (model.Workspace != null) await model.Workspace.JoinExistingCallAsync(item.Message.CallSummary!.CallId); }));
        if (item.HasAttachments)
        {
            var download = new MenuItem { Header = "Download attachment" };
            foreach (var attachment in item.Attachments) download.Items.Add(new MenuItem { Header = attachment.FileName, Command = attachment.DownloadCommand });
            menu.Items.Add(new MenuItem { Header = "Download All", Command = item.DownloadAllCommand });
            menu.Items.Add(download);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Action("Message details", () => ShowDetails(model, item, target)));
        if (item.CanModify)
        {
            menu.Items.Add(new Separator());
            var delete = Async("Delete message…", async () =>
            {
                if (TopLevel.GetTopLevel(target) is not Window owner) return;
                var confirmation = new MessageDialog(new("Delete this message?", "This removes it for everyone in this conversation.", item.Text ?? "Attachment", "This action cannot be undone.", true));
                if (await confirmation.ShowDialog<bool>(owner)) await model.DeleteMessage(item);
            });
            delete.Classes.Add("danger"); menu.Items.Add(delete);
        }
        return menu;
    }
    internal static void ShowDetails(ChatViewModel model, ChatMessageItemViewModel item, Control target)
    {
        var window = new MessageDetailsWindow(model.CreateMessageDetails(item));
        if (TopLevel.GetTopLevel(target) is Window owner) window.ShowDialog(owner); else window.Show();
    }
    internal static void Open(Control target, ContextMenu menu, bool atPointer)
    {
        target.ContextMenu?.Close(); target.ContextMenu = menu;
        menu.Placement = atPointer ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedRight;
        menu.Open(target);
    }
}
