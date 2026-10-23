using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;

public sealed class AttachmentPreviewControlTests
{
    private static Button Button(Control root,string label)=>root.GetVisualDescendants().OfType<Button>().Single(x=>x.Content?.ToString()==label);
    private static void Click(Control root,string label)=>Button(root,label).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static Bitmap Photo()
    {
        using var image=new WriteableBitmap(new PixelSize(640,360),new Vector(96,96),Avalonia.Platform.PixelFormat.Rgba8888,Avalonia.Platform.AlphaFormat.Opaque);
        using(var frame=image.Lock()) {var pixels=new byte[640*360*4];for(var i=0;i<pixels.Length;i+=4){pixels[i]=35;pixels[i+1]=110;pixels[i+2]=210;pixels[i+3]=255;}System.Runtime.InteropServices.Marshal.Copy(pixels,0,frame.Address,pixels.Length);}
        using var file=new MemoryStream();image.Save(file,new PngBitmapEncoderOptions());file.Position=0;return new Bitmap(file);
    }
    [AvaloniaFact]
    public void PhotosHaveFunctionalZoomRotationFitActualSizeAndScrollNavigation()
    {
        using var photo=Photo();var viewer=new PhotoPreview(photo);var window=new Window{Width=900,Height=650,Content=viewer};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            Click(viewer,"Actual size");Assert.Equal(1,viewer.Zoom);Click(viewer,"Zoom in");Assert.Equal(1.25,viewer.Zoom);Click(viewer,"Zoom out");Assert.Equal(1,viewer.Zoom);
            Click(viewer,"Rotate right");Assert.Equal(90,viewer.Rotation);Assert.Equal(90,Assert.IsType<RotateTransform>(Assert.Single(viewer.GetVisualDescendants().OfType<Image>()).RenderTransform).Angle);
            Click(viewer,"Rotate left");Assert.Equal(0,viewer.Rotation);Click(viewer,"Fit");Assert.True(viewer.Zoom>0);
            viewer.SetZoom(100);Assert.Equal(16,viewer.Zoom);Dispatcher.UIThread.RunJobs();var scroll=Assert.Single(viewer.GetVisualDescendants().OfType<ScrollViewer>());Assert.True(scroll.Extent.Width>scroll.Viewport.Width);
            scroll.Offset=new Vector(100,50);Assert.Equal(100,scroll.Offset.X);Assert.Equal(50,scroll.Offset.Y);
            viewer.SetZoom(0);Assert.Equal(.01,viewer.Zoom);var requested=0;viewer.FullscreenRequested+=()=>requested++;Click(viewer,"Fullscreen");Assert.Equal(1,requested);
        }
        finally {window.Close();}
    }
    [Theory][InlineData(0,0,0)][InlineData(.5,16383,-16384)][InlineData(1,32767,-32768)]
    public void VolumeAppliesToActualPcmSamples(double gain,int positive,int negative)
    {
        var pcm=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(0,2),32767);System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2,2),-32768);
        AudioNotePlayback.ApplyVolume(pcm,gain);Assert.Equal(positive,System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0,2)));Assert.Equal(negative,System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2,2)));
    }
    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task OpaqueGalleryUsesFullClientAreaAndPhotoControlsInBothThemes(bool dark)
    {
        using var bitmap=Photo();using var png=new MemoryStream();bitmap.Save(png,new PngBitmapEncoderOptions());
        var chat=Substitute.For<IChatService>();chat.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(async c=>{await File.WriteAllBytesAsync(c.ArgAt<string>(1),png.ToArray(),c.ArgAt<CancellationToken>(3));return true;});
        var item=new ChatAttachmentItemViewModel(new ChatAttachment{Id=Guid.NewGuid(),FileName="Landscape.png",MimeType="image/png"},chat,Substitute.For<IFileService>(),Substitute.For<IImageService>());
        var owner=new Window{RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light};owner.Show();var gallery=new AttachmentGalleryWindow(new[]{item});gallery.Show(owner);await gallery.SelectAsync(0);Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(WindowTransparencyLevel.None,Assert.Single(gallery.TransparencyLevelHint));Assert.Equal(255,Assert.IsAssignableFrom<ISolidColorBrush>(gallery.Background).Color.A);Assert.Equal(WindowDecorations.Full,gallery.WindowDecorations);
            var surface=Assert.IsType<Border>(gallery.Content);Assert.Equal(HorizontalAlignment.Stretch,surface.HorizontalAlignment);Assert.Equal(double.PositiveInfinity,surface.MaxWidth);Assert.Equal(255,Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color.A);
            var photo=Assert.Single(gallery.GetVisualDescendants().OfType<PhotoPreview>());Click(photo,"Rotate right");Assert.Equal(90,photo.Rotation);Click(photo,"Fit");
            Click(photo,"Fullscreen");Assert.Equal(WindowState.FullScreen,gallery.WindowState);Click(gallery,"Exit fullscreen");Assert.NotEqual(WindowState.FullScreen,gallery.WindowState);
            Assert.DoesNotContain(gallery.GetVisualDescendants().OfType<Button>(),x=>x.Content?.ToString() is "Close" or "X" or "×");
            if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} dir){Directory.CreateDirectory(dir);using var frame=gallery.CaptureRenderedFrame();Assert.NotNull(frame);using var output=File.Create(Path.Combine(dir,$"opaque-photo-preview-{(dark?"dark":"light")}.png"));frame.Save(output,new PngBitmapEncoderOptions());}
        }
        finally {var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);gallery.Closed+=(_,_)=>closed.TrySetResult();gallery.Close();await closed.Task;owner.Close();}
    }
    [AvaloniaFact]
    public async Task MusicAndVideoExposeVisibleSeekVolumeStopAndPlaybackControls()
    {
        var audio=new InlineAudioPlayer{IsPreview=true,IsSent=false,Width=640};var window=new Window{Content=audio,Width=800,Height=500};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            var seek=audio.GetVisualDescendants().OfType<Slider>().Single(s=>AutomationProperties.GetName(s)=="Seek audio");Assert.Equal(1,seek.Opacity);
            var volume=audio.GetVisualDescendants().OfType<Slider>().Single(s=>AutomationProperties.GetName(s)=="Audio volume");volume.Value=25;Assert.Equal(.25,audio.PlaybackVolume);Click(audio,"Stop");await audio.StopPlaybackAsync();Assert.Equal(0,seek.Value);
            Assert.Contains(audio.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="0:00 / 0:00");Assert.Contains(audio.GetVisualDescendants().OfType<Button>(),b=>AutomationProperties.GetName(b)=="Play audio");Assert.Contains(audio.GetVisualDescendants().OfType<Button>(),b=>AutomationProperties.GetName(b).StartsWith("Playback speed"));
            var video=new InlineVideoPlayer();window.Content=video;Dispatcher.UIThread.RunJobs();var videoVolume=video.GetVisualDescendants().OfType<Slider>().Single(s=>AutomationProperties.GetName(s)=="Video volume");videoVolume.Value=0;Assert.Equal(0,video.Volume);videoVolume.Value=75;Assert.Equal(.75,video.Volume);
            Assert.Contains(video.GetVisualDescendants().OfType<Slider>(),s=>AutomationProperties.GetName(s)=="Seek video");Click(video,"Stop");Assert.NotNull(Button(video,"Play"));var full=0;video.FullscreenRequested+=()=>full++;Click(video,"Fullscreen");Assert.Equal(1,full);await video.StopAsync();window.Content=audio;Dispatcher.UIThread.RunJobs();Assert.Single(audio.GetVisualDescendants().OfType<Slider>().Where(s=>AutomationProperties.GetName(s)=="Seek audio"));Assert.NotNull(Button(audio,"Stop"));
        }
        finally {window.Close();}
    }
}
