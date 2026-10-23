using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class ChatEmojiAndSpacingTests
{
    [AvaloniaFact]
    public void ButtonsHaveRealGapsAndExplicitSpacingIsPreserved()
    {
        var defaults=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal};defaults.Children.Add(new Button{Content="Approve"});defaults.Children.Add(new Button{Content="Reject"});
        var explicitGroup=new StackPanel{Spacing=14,Orientation=Avalonia.Layout.Orientation.Horizontal};explicitGroup.Children.Add(new Button{Content="Save"});explicitGroup.Children.Add(new Button{Content="Cancel"});
        var wrap=new WrapPanel();wrap.Children.Add(new Button{Content="Invite"});wrap.Children.Add(new Button{Content="Done"});
        var root=new StackPanel();root.Children.Add(defaults);root.Children.Add(explicitGroup);root.Children.Add(wrap);var window=new Window{Width=600,Height=300,Content=root};window.Show();Dispatcher.UIThread.RunJobs();
        try {Assert.True(defaults.Children[1].Bounds.X-defaults.Children[0].Bounds.Right>=8);Assert.Equal(14,explicitGroup.Spacing);Assert.Equal(8,wrap.ItemSpacing);Assert.Equal(8,wrap.LineSpacing);Assert.True(wrap.Children[1].Bounds.X-wrap.Children[0].Bounds.Right>=8);}
        finally {window.Close();}
    }
    [AvaloniaFact]
    public void EmojiPickerSearchCategoriesEmptyStateAndSelectionAreUsable()
    {
        var picker=new EmojiPicker();var window=new Window{Content=picker,Width=380,Height=430};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} dir){Directory.CreateDirectory(dir);using var frame=Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);Assert.NotNull(frame);using var file=File.Create(Path.Combine(dir,"desktop-emoji-picker.png"));frame.Save(file,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
            var search=picker.GetVisualDescendants().OfType<TextBox>().Single(x=>x.Name=="EmojiSearch");search.Text="rocket";Dispatcher.UIThread.RunJobs();var rocket=Assert.Single(picker.GetVisualDescendants().OfType<Button>(),b=>b.Content?.ToString()=="🚀");string? selected=null;picker.Selected+=value=>selected=value;rocket.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal("🚀",selected);
            search.Text="not-an-emoji-name";Dispatcher.UIThread.RunJobs();Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="No matching emojis"&&t.IsVisible);
            search.Text="";picker.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Faces").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();Assert.Contains(picker.GetVisualDescendants().OfType<Button>(),b=>b.Content?.ToString()=="😀");Assert.DoesNotContain(picker.GetVisualDescendants().OfType<Button>(),b=>b.Content?.ToString()=="🚀");
        }
        finally {window.Close();}
    }
    [AvaloniaTheory]
    [InlineData("Hello there",6,11,"Hello 😀")]
    [InlineData("Hi ",3,3,"Hi 😀")]
    [InlineData("A😀B",2,2,"A😀B")]
    public void EmojiInsertionReplacesSelectionPreservesBindingAndDoesNotSplitSurrogates(string text,int start,int end,string expected)
    {
        var draft=new DesktopIconographyTests.ActionState{Label=text};var input=new TextBox();input.Bind(TextBox.TextProperty,new Binding(nameof(draft.Label)){Source=draft,Mode=BindingMode.TwoWay});
        EmojiPicker.Insert(input,"😀",start,end);Assert.Equal(expected,input.Text);Assert.Equal(expected,draft.Label);Assert.Equal(input.SelectionStart,input.SelectionEnd);
    }
    [AvaloniaFact]
    public async Task ComposerInsertsEmojiWithoutSendingAndClosesPickerWhenConversationChanges()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});var chat=Substitute.For<IChatService>();
        var model=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        model.SelectedRecipient=new RecipientItemViewModel{ReceiverId=Guid.NewGuid(),ReceiverName="Colleague"};model.MessageDraft="Hi ";var view=new ChatView{DataContext=model};var window=new Window{Content=view,Width=1150,Height=800};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            var input=view.FindControl<TextBox>("MessageInputBox")!;input.SelectionStart=input.SelectionEnd=3;var button=view.FindControl<Button>("EmojiButton")!;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();var picker=Assert.IsType<EmojiPicker>(view.ActiveEmojiPicker);
            picker.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="😀").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();Assert.Equal("Hi 😀",model.MessageDraft);await chat.DidNotReceive().SendMessageAsync(Arg.Any<Guid>(),Arg.Any<string>());
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();Assert.NotNull(view.ActiveEmojiPicker);model.SelectedRecipient=new RecipientItemViewModel{ReceiverId=Guid.NewGuid(),ReceiverName="Other"};Assert.Null(view.ActiveEmojiPicker);
        }
        finally {window.Close();model.UnregisterEvents();}
    }
}
