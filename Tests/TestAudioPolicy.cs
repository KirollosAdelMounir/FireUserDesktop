using System.Runtime.CompilerServices;
namespace UserIdentityDesktop.Tests;
internal static class TestAudioPolicy
{
    [ModuleInitializer] internal static void Initialize() => AppContext.SetSwitch("FireUser.DisableAudio", true);
}
