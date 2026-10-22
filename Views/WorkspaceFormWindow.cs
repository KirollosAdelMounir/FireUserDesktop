using System;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Input;
using Avalonia.VisualTree;
using UserIdentityDesktop.Converters;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

internal sealed class WorkspaceFormWindow : Window
{
    private readonly WindowMotion motion;
    private readonly TaskCompletionSource closed = new();
    public Control Editor { get; }
    public WorkspaceFormKind Kind { get; }
    public WorkspaceFormWindow(Control editor, WorkspaceViewModel model, WorkspaceFormKind kind)
    {
        Editor = editor; Kind = kind; DataContext = model;
        Title = kind switch { WorkspaceFormKind.Calendar => "New schedule", WorkspaceFormKind.Vacation => "Request vacation", _ => "New announcement" };
        Width = 620; Height = 740; MinWidth = 420; MinHeight = 400;
        if (kind == WorkspaceFormKind.Vacation) { SizeToContent = SizeToContent.Height; Height = double.NaN; MinHeight = 360; }
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontSize = 13;
        this.Bind(BackgroundProperty, this.GetResourceObservable("ForgeBg")); this.Bind(ForegroundProperty, this.GetResourceObservable("ForgeTextPrimary"));
        editor.DataContext = model; editor.IsVisible = true; editor.Margin = new Thickness(24); editor.MaxHeight = double.PositiveInfinity;
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 8), MaxHeight = 90 };
        feedback.Bind(TextBlock.TextProperty, new Binding(nameof(model.Feedback)));
        feedback.Bind(IsVisibleProperty, new Binding(nameof(model.HasWorkspaceStatus)));
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3 };
        progress.Bind(IsVisibleProperty, new Binding(nameof(model.IsLoading)));
        var footerButtons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        footerButtons.Bind(IsEnabledProperty, new Binding(nameof(model.IsLoading)) { Converter = BoolInverterConverter.Instance });
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _ = CloseAnimatedAsync(); footerButtons.Children.Add(cancel);
        if (kind == WorkspaceFormKind.Calendar) footerButtons.Children.Add(new Button { Content = "Review availability", Command = model.ReviewEventCommand });
        if (kind == WorkspaceFormKind.Vacation) footerButtons.Children.Add(new Button { Content = "Preview dates and balance", Command = model.PreviewVacationCommand });
        var submit = new Button { Content = kind switch { WorkspaceFormKind.Calendar => "Save schedule", WorkspaceFormKind.Vacation => "Submit request", _ => "Publish announcement" }, Command = kind switch { WorkspaceFormKind.Calendar => model.SaveEventCommand, WorkspaceFormKind.Vacation => model.SubmitVacationCommand, _ => model.PublishCommand } };
        submit.Classes.Add("fire-btn");
        if (kind == WorkspaceFormKind.Calendar) submit.Bind(IsEnabledProperty, new Binding(nameof(model.Reviewed)));
        if (kind == WorkspaceFormKind.Vacation) submit.Bind(IsEnabledProperty, new Binding(nameof(model.VacationReview)) { Converter = NullToBoolConverter.Instance });
        footerButtons.Children.Add(submit);
        if (editor is ScrollViewer { Content: StackPanel fields })
            foreach (var button in fields.Children.OfType<Button>())
                if (button.Command == submit.Command || kind == WorkspaceFormKind.Calendar && button.Command == model.ReviewEventCommand || kind == WorkspaceFormKind.Vacation && button.Command == model.PreviewVacationCommand) button.IsVisible = false;
        var footer = new Border { Child = footerButtons, Padding = new Thickness(24, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.Bind(Border.BorderBrushProperty, this.GetResourceObservable("ForgeBorder")); footer.Bind(Border.BackgroundProperty, this.GetResourceObservable("ForgeSurface"));
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        layout.Children.Add(progress); layout.Children.Add(editor); layout.Children.Add(feedback); Grid.SetRow(editor, 1); Grid.SetColumn(editor, 0); Grid.SetRowSpan(editor, 1); Grid.SetRow(feedback, 2); layout.Children.Add(footer); Grid.SetRow(footer, 3);
        Content = layout; motion = new WindowMotion(this);
        Opened += (_, _) => editor.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
        Closed += (_, _) => closed.TrySetResult();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _ = CloseAnimatedAsync(); } };
    }
    public async Task CloseAnimatedAsync() { await motion.CloseAsync<object?>(null); await closed.Task; }
}
