using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.Service.Media;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;

namespace UserIdentityDesktop.Tests;
public sealed class VoiceNoteTests
{
    private static byte[] Tone(double seconds)
    {
        var bytes = new byte[(int)(48000 * seconds) * 2];
        for (var i = 0; i < bytes.Length / 2; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 6000));
        return bytes;
    }
    internal static VoiceNoteRecorder Recorder(out Channel<byte[]> frames)
    {
        var input = Channel.CreateUnbounded<byte[]>(); frames = input;
        return new VoiceNoteRecorder(input.Reader, () => input.Writer.TryComplete());
    }
    [Fact]
    public async Task SyntheticVoice_IsRealM4aWithAudioMetadata_DecodesAndSeeks_AndCleansUp()
    {
        await using var note = Recorder(out var frames); await frames.Writer.WriteAsync(Tone(.4), TestContext.Current.CancellationToken);
        var path = await note.StopAsync(); Assert.Equal(path, await note.StopAsync());
        Assert.EndsWith(".m4a", path); Assert.False(File.Exists(note.RawPath));
        var header = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken); Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(header, 4, 4));
        var duration = await AudioNotePlayback.DurationAsync(path, TestContext.Current.CancellationToken); Assert.InRange(duration, .35, .5);
        long decoded = 0; double position = 0;
        await AudioNotePlayback.DecodeAsync(path, .2, bytes => decoded += bytes.Length, value => position = value, TestContext.Current.CancellationToken);
        Assert.InRange(decoded, 17000, 25000); Assert.InRange(position, .38, .5);
        var encrypted = await ChatEncryption.PrepareFileAsync(path);
        try { Assert.Equal("audio/mp4", encrypted.Metadata.MimeType); Assert.Equal(48, encrypted.Metadata.Waveform!.Length); Assert.Contains(encrypted.Metadata.Waveform, peak => peak > .1); } finally { File.Delete(encrypted.Path); }
        await note.DisposeAsync(); Assert.False(File.Exists(path));
    }
    [Theory]
    [InlineData(.5)] [InlineData(1)] [InlineData(1.5)] [InlineData(2)]
    public async Task PlaybackSpeed_ChangesDurationAndPreservesPitchAndSourceTimeline(double speed)
    {
        await using var note = Recorder(out var frames); await frames.Writer.WriteAsync(Tone(.8), TestContext.Current.CancellationToken);
        var path = await note.StopAsync(); long decoded = 0; var crossings = 0; short previous = 0; double position = 0;
        await AudioNotePlayback.DecodeAsync(path, 0, bytes =>
        {
            decoded += bytes.Length;
            for (var i = 0; i < bytes.Length; i += 2)
            {
                var sample = BitConverter.ToInt16(bytes, i); if (previous < 0 && sample >= 0) crossings++; previous = sample;
            }
        }, value => position = value, TestContext.Current.CancellationToken, speed);
        var seconds = decoded / 96000d;
        Assert.InRange(seconds, .8 / speed - .12, .8 / speed + .12);
        Assert.InRange(crossings / seconds, 400, 480); Assert.InRange(position, .68, .95);
    }
    [Fact]
    public async Task Waveform_RepresentsRealSilenceAndSoundIn48BoundedPeaks()
    {
        await using var note = Recorder(out var frames);
        await frames.Writer.WriteAsync(new byte[48000], TestContext.Current.CancellationToken); await frames.Writer.WriteAsync(Tone(.5), TestContext.Current.CancellationToken);
        var path = await note.StopAsync(); var duration = await AudioNotePlayback.DurationAsync(path, TestContext.Current.CancellationToken);
        var peaks = await AudioNotePlayback.WaveformAsync(path, duration, TestContext.Current.CancellationToken);
        Assert.Equal(48, peaks.Length); Assert.All(peaks, peak => Assert.InRange(peak, 0, 1));
        Assert.All(peaks.Take(20), peak => Assert.True(peak < .02)); Assert.All(peaks.Skip(28), peak => Assert.True(peak > .1));
    }
    [AvaloniaFact]
    public void VoicePlayer_CyclesAngularSpeedChoices_AndUsesTheSuppliedWaveformBeforeDownloading()
    {
        var metadata = new EncryptedFile(Guid.NewGuid().ToString(), "voice.m4a", "audio/mp4", 0, 1048576, "key", "nonce") { Waveform = Enumerable.Range(0, 48).Select(i => i / 48d).ToArray() };
        var service = Substitute.For<IChatService>();
        var attachment = new ChatAttachmentItemViewModel(new ChatAttachment { FileName = "voice.m4a", MimeType = "audio/mp4", Encryption = metadata }, service, Substitute.For<IFileService>(), Substitute.For<IImageService>());
        var player = new InlineAudioPlayer { Attachment = attachment }; var window = new Window { Content = player, Width = 310, Height = 72 }; window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(metadata.Waveform, player.GetVisualDescendants().OfType<VoiceWaveform>().Single().Peaks);
        var button = player.GetVisualDescendants().OfType<Button>().Single(control => Avalonia.Automation.AutomationProperties.GetName(control)?.StartsWith("Playback speed") == true);
        foreach (var value in new[] { 1.5, 2, .5, 1 })
        {
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.Equal(value, player.PlaybackSpeed);
        }
        service.DidNotReceive().SaveAttachmentToFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<EncryptedFile?>());
        if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} directory)
        {
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            using var file = File.Create(Path.Combine(directory, "voice-waveform-player.png")); frame!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        window.Close();
    }
    [Fact]
    public async Task EmptyOrDiscardedRecording_DeletesRawCaptureAndDoesNotSendAWav()
    {
        var note = Recorder(out _); await Assert.ThrowsAsync<InvalidOperationException>(() => note.StopAsync());
        try { await note.DisposeAsync(); } catch (InvalidOperationException) { }
        Assert.False(File.Exists(note.Path)); Assert.False(File.Exists(note.RawPath));
        var discarded = Recorder(out var frames); await frames.Writer.WriteAsync(Tone(.1), TestContext.Current.CancellationToken);
        await discarded.DisposeAsync(); await discarded.DisposeAsync();
        Assert.False(File.Exists(discarded.Path)); Assert.False(File.Exists(discarded.RawPath));
    }
    [AvaloniaFact]
    public async Task VoiceComposer_SendsAudioOnly_RetainsFailedUpload_AndDiscardsOnConversationChange()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        var service = Substitute.For<IChatService>();
        var vm = new ChatViewModel(auth, service, Substitute.For<IUserService>(), Substitute.For<IImageService>(), Substitute.For<IFileService>(), Substitute.For<INotificationService>());
        vm.SelectedRecipient = new RecipientItemViewModel { ReceiverId = Guid.NewGuid(), ReceiverName = "Peer" }; Dispatcher.UIThread.RunJobs();
        vm.MessageDraft = "Keep this text draft";
        vm.CreateVoiceRecorderAsync = _ => { var recorder = Recorder(out var input); input.Writer.TryWrite(Tone(.2)); return Task.FromResult(recorder); };
        await vm.ToggleVoiceRecordingCommand.ExecuteAsync(null); Assert.True(vm.IsRecordingVoice); Assert.False(vm.CanTypeMessage);
        await vm.ToggleVoiceRecordingCommand.ExecuteAsync(null); Assert.True(vm.HasVoiceNote); Assert.False(vm.CanTypeMessage);
        var path = vm.VoiceNotePath!; Assert.True(File.Exists(path));
        service.SendMessageWithAttachmentsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(false);
        await vm.SendMessageCommand.ExecuteAsync(null); Assert.Equal(path, vm.VoiceNotePath); Assert.True(File.Exists(path));
        await service.Received(1).SendMessageWithAttachmentsAsync(Arg.Any<Guid>(), null, Arg.Is<IEnumerable<string>>(files => files.Single() == path), null, null);
        service.SendMessageWithAttachmentsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
        await vm.SendMessageCommand.ExecuteAsync(null); Assert.False(vm.HasVoiceNote); Assert.True(vm.CanTypeMessage); Assert.False(File.Exists(path)); Assert.Equal("Keep this text draft", vm.MessageDraft);
        await vm.ToggleVoiceRecordingCommand.ExecuteAsync(null); await vm.ToggleVoiceRecordingCommand.ExecuteAsync(null); path = vm.VoiceNotePath!;
        vm.SelectedRecipient = new RecipientItemViewModel { ReceiverId = Guid.NewGuid() }; Dispatcher.UIThread.RunJobs();
        Assert.False(vm.HasVoiceNote); Assert.False(File.Exists(path)); vm.UnregisterEvents();
    }
    [Fact]
    public async Task PlaybackCancellation_StopsTheDecoderWithoutAnyAudioDevice()
    {
        await using var note = Recorder(out var frames); await frames.Writer.WriteAsync(Tone(1), TestContext.Current.CancellationToken);
        var path = await note.StopAsync(); using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioNotePlayback.DecodeAsync(path, 0, _ => stop.Cancel(), _ => {}, stop.Token));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task InlinePlayback_UsesAuthenticatedDecryption_AndDeletesFailedTemporaryDownloads(bool success)
    {
        var service = Substitute.For<IChatService>(); var files = Substitute.For<IFileService>(); var image = Substitute.For<IImageService>();
        var metadata = new EncryptedFile(Guid.NewGuid().ToString(), "voice.m4a", "audio/mp4", 10, 1048576, "key", "nonce");
        var attachment = new ChatAttachment { Id = Guid.NewGuid(), FileName = "voice.m4a", MimeType = "audio/mp4", Encryption = metadata };
        string? destination = null;
        service.SaveAttachmentToFileAsync(attachment.Id, Arg.Any<string>(), metadata).Returns(call =>
        {
            destination = call.ArgAt<string>(1); File.WriteAllBytes(destination, new byte[] { 1, 2, 3 }); return success;
        });
        var vm = new ChatAttachmentItemViewModel(attachment, service, files, image);
        try
        {
            if (success) { var path = await vm.PrepareAudioPlaybackAsync(); Assert.Equal(destination, path); Assert.True(File.Exists(path)); }
            else { await Assert.ThrowsAsync<IOException>(() => vm.PrepareAudioPlaybackAsync()); Assert.False(File.Exists(destination)); }
            await files.DidNotReceive().PickSaveFileAsync(Arg.Any<string>(), Arg.Any<string>());
        }
        finally { if (destination != null) File.Delete(destination); }
    }
    [AvaloniaFact]
    public async Task ChangingConversationDuringMicrophoneStartup_DiscardsTheLateRecorder()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        var vm = new ChatViewModel(auth, Substitute.For<IChatService>(), Substitute.For<IUserService>(), Substitute.For<IImageService>(), Substitute.For<IFileService>(), Substitute.For<INotificationService>());
        vm.SelectedRecipient = new RecipientItemViewModel { ReceiverId = Guid.NewGuid() }; Dispatcher.UIThread.RunJobs();
        var startup = new TaskCompletionSource<VoiceNoteRecorder>(); vm.CreateVoiceRecorderAsync = _ => startup.Task;
        var opening = vm.ToggleVoiceRecordingCommand.ExecuteAsync(null); Assert.True(vm.IsOpeningMicrophone);
        vm.SelectedRecipient = new RecipientItemViewModel { ReceiverId = Guid.NewGuid() }; Dispatcher.UIThread.RunJobs();
        var note = Recorder(out var input); input.Writer.TryWrite(Tone(.1)); startup.SetResult(note); await opening;
        Assert.False(vm.VoiceComposerActive); Assert.False(File.Exists(note.RawPath)); Assert.False(File.Exists(note.Path)); vm.UnregisterEvents();
    }
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task AngularVoiceDesign_RendersSentReceivedRecordingAndReadyStates(bool light)
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() }); var service = Substitute.For<IChatService>();
        service.GetChatHistoryAsync(Arg.Any<Guid>(), Arg.Any<int>()).Returns(new List<ChatMessage>());
        var images = Substitute.For<IImageService>(); var files = Substitute.For<IFileService>();
        var vm = new ChatViewModel(auth, service, Substitute.For<IUserService>(), images, files, Substitute.For<INotificationService>());
        var workspace = new WorkspaceViewModel(vm, Substitute.For<ICollaborationApi>(), Substitute.For<INativeMediaSession>(), auth, service) { ReduceMotion = true };
        vm.SelectedRecipient = new RecipientItemViewModel { ReceiverId = Guid.NewGuid(), ReceiverName = "Colleague" }; Dispatcher.UIThread.RunJobs();
        foreach (var mine in new[] { false, true })
        {
            var metadata = new EncryptedFile(Guid.NewGuid().ToString(), "voice-note.m4a", "audio/mp4", 10, 1048576, "key", "nonce") { Waveform = Enumerable.Range(0, 48).Select(i => .1 + .7 * Math.Abs(Math.Sin(i * .7))).ToArray() };
            var message = new ChatMessage { MessageId = Guid.NewGuid().ToString(), Sender = mine ? vm.CurrentUser.Id : vm.SelectedRecipient.ReceiverId, Receiver = mine ? vm.SelectedRecipient.ReceiverId : vm.CurrentUser.Id, TimeSent = DateTime.UtcNow, Attachments = new() { new ChatAttachment { Id = Guid.NewGuid(), FileName = "voice-note.m4a", MimeType = "audio/mp4", Encryption = metadata } } };
            vm.Messages.Add(new ChatMessageItemViewModel(message, mine, service, files, images));
        }
        var view = new ChatView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 700, RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark }; window.Show(); Dispatcher.UIThread.RunJobs();
        var players = view.GetVisualDescendants().OfType<InlineAudioPlayer>().Where(player => player.Attachment != null).ToArray();
        Assert.Equal(2, players.Length); Assert.All(players, player => Assert.InRange(player.Bounds.Width, 300, 310)); Assert.Single(players, player => player.IsSent); Assert.Single(players, player => !player.IsSent);
        vm.CreateVoiceRecorderAsync = _ => { var note = Recorder(out var frames); frames.Writer.TryWrite(Tone(1.1)); return Task.FromResult(note); };
        await vm.ToggleVoiceRecordingCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); Assert.True(view.FindControl<Border>("VoiceComposer")!.IsVisible);
        SaveVoiceFrame(window, $"voice-recording-{(light ? "light" : "dark")}.png");
        await vm.ToggleVoiceRecordingCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
        var preview = view.GetVisualDescendants().OfType<InlineAudioPlayer>().Single(player => player.LocalPath != null);
        for (var i = 0; i < 30 && preview.GetVisualDescendants().OfType<VoiceWaveform>().Single().Peaks == null; i++) { await Task.Delay(50, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(48, preview.GetVisualDescendants().OfType<VoiceWaveform>().Single().Peaks!.Length);
        SaveVoiceFrame(window, $"voice-ready-{(light ? "light" : "dark")}.png");
        await vm.DiscardVoiceNoteCommand.ExecuteAsync(null); window.Close(); vm.UnregisterEvents(); await workspace.DisposeAsync();
    }
    private static void SaveVoiceFrame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is not {} directory) return;
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        using var file = File.Create(Path.Combine(directory, name)); frame!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    [AvaloniaFact]
    public void AudioAttachments_HaveAnInlinePlayer_WithoutOpeningDevicesOrExternalApplications()
    {
        var service = Substitute.For<IChatService>(); var files = Substitute.For<IFileService>(); var images = Substitute.For<IImageService>();
        var audio = new ChatAttachmentItemViewModel(new ChatAttachment { FileName = "voice-note-old.wav", MimeType = "application/octet-stream" }, service, files, images);
        Assert.True(audio.IsAudio);
        var player = new InlineAudioPlayer { Attachment = audio }; var window = new Window { Content = player, Width = 400, Height = 80 }; window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(player.GetVisualDescendants().OfType<Button>(), button => Avalonia.Automation.AutomationProperties.GetName(button) == "Play voice note");
        if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} directory)
        {
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            using var file = File.Create(Path.Combine(directory, "inline-audio-player.png")); frame!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        service.DidNotReceive().SaveAttachmentToFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<EncryptedFile?>());
        window.Close();
    }
}
