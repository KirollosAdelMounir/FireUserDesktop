using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed partial class DesktopIconographyTests
{
    public sealed partial class ActionState:ObservableObject { [ObservableProperty] private string label="Play"; }
    [AvaloniaFact]
    public void BoundActionKeepsLabelCommandAndParameterWhileIconFollowsState()
    {
        var state=new ActionState();object? sent=null;var button=new Button{Command=new RelayCommand<object?>(value=>sent=value),CommandParameter="original"};button.Bind(ContentControl.ContentProperty,new Binding(nameof(ActionState.Label)){Source=state});
        var window=new Window{Content=button,Width=350,Height=150};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal("Play",button.Content);var icon=Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());Assert.NotNull(icon.Data);var shape=icon.Data;
            state.Label="Pause";Dispatcher.UIThread.RunJobs();Assert.Equal("Pause",button.Content);Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Pause");icon=Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());Assert.NotEqual(shape,icon.Data);
            var center=button.TranslatePoint(new Point(button.Bounds.Width/2,button.Bounds.Height/2),window)!.Value;window.MouseDown(center,MouseButton.Left);window.MouseUp(center,MouseButton.Left);Assert.Equal("original",sent);
        }
        finally {window.Close();}
    }
    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public void SharedActionsCheckboxTabsAndMenusShowIconsWithoutDuplicatingCustomContent(bool dark)
    {
        var actions=new StackPanel{Spacing=12};foreach(var label in new[]{"Approve","Reject","Invite people","Save schedule","Settings","Zoom in","Play","Stop","Download All"})actions.Children.Add(new Button{Content=label});
        var check=new CheckBox{Content="Join with microphone muted"};actions.Children.Add(check);
        var existing=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal};existing.Children.Add(AttachmentIcons.Create("Phone"));existing.Children.Add(new TextBlock{Text="Call"});var custom=new Button{Content=existing};actions.Children.Add(custom);
        var emoji=new Button{Content="👍"};var day=new Button{Content="23"};actions.Children.Add(emoji);actions.Children.Add(day);
        var tab=new TabItem{Header="Calendar"};var tabs=new TabControl{ItemsSource=new[]{tab}};actions.Children.Add(tabs);
        var window=new Window{Content=actions,Width=500,Height=950,RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            foreach(var button in actions.Children.OfType<Button>().Where(x=>x.Content is string label&&char.IsLetter(label[0])))Assert.NotNull(Assert.Single(button.GetVisualDescendants().OfType<PathIcon>()).Data);
            Assert.Single(custom.GetVisualDescendants().OfType<PathIcon>());Assert.Null(custom.ContentTemplate);Assert.Empty(emoji.GetVisualDescendants().OfType<PathIcon>());Assert.Empty(day.GetVisualDescendants().OfType<PathIcon>());Assert.Single(tab.GetVisualDescendants().OfType<PathIcon>());
            var entry=new MenuItem{Header="My profile"};var menu=new ContextMenu();menu.Items.Add(entry);menu.Open(actions);Dispatcher.UIThread.RunJobs();Assert.NotNull(Assert.IsType<PathIcon>(entry.Icon).Data);entry.Header="Sign out";Dispatcher.UIThread.RunJobs();Assert.NotNull(Assert.IsType<PathIcon>(entry.Icon).Data);
            var own=new PathIcon{Data=Geometry.Parse("M0,0 L10,10 0,10 Z")};entry.Icon=own;entry.Header="Settings";Assert.Same(own,entry.Icon);menu.Close();
            using var native=DesktopIconography.NativeIcon("Sign out",window);Assert.Equal(new PixelSize(20,20),native.PixelSize);
            using(var raster=new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(20,20),new Vector(96,96),Avalonia.Platform.PixelFormat.Rgba8888,Avalonia.Platform.AlphaFormat.Premul))
            using(var pixels=raster.Lock()){native.CopyPixels(pixels);var data=new byte[pixels.RowBytes*20];System.Runtime.InteropServices.Marshal.Copy(pixels.Address,data,0,data.Length);Assert.Contains(Enumerable.Range(0,20*20),i=>data[(i/20)*pixels.RowBytes+(i%20)*4+3]>0);}
            if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} dir){Directory.CreateDirectory(dir);using var image=window.CaptureRenderedFrame();Assert.NotNull(image);using var file=File.Create(Path.Combine(dir,$"desktop-action-icons-{(dark?"dark":"light")}.png"));image.Save(file,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
        }
        finally {window.Close();}
    }
    [AvaloniaFact]
    public void ExplicitTemplatesStayIntactAndChangingStringToCustomContentRemovesAutomaticTemplate()
    {
        var template=new FuncDataTemplate<string>((text,_)=>new TextBlock{Text="Custom "+text});var own=new Button{Content="Save",ContentTemplate=template};var automatic=new Button{Content="Save"};var panel=new StackPanel();panel.Children.Add(own);panel.Children.Add(automatic);var window=new Window{Content=panel};window.Show();Dispatcher.UIThread.RunJobs();
        try {Assert.Same(template,own.ContentTemplate);Assert.NotNull(automatic.ContentTemplate);var icon=AttachmentIcons.Create("Phone");automatic.Content=icon;Dispatcher.UIThread.RunJobs();Assert.Null(automatic.ContentTemplate);Assert.Same(icon,automatic.Content);Assert.Single(automatic.GetVisualDescendants().OfType<PathIcon>());}
        finally {window.Close();}
    }
}
