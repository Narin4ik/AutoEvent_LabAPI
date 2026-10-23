using AutoEvent.Integrations.Audio;
using LabApi.Features.Wrappers;
using SecretLabNAudio.Core;
using SecretLabNAudio.Core.Pools;

internal static class AudioPlaybackChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var music = SlNaExtensions.PlayAudio("track.ogg", "track.ogg", true, false, 5, 5000, default);
        AudioPlayer player = AudioPlayerPool.Last;
        check(player.Source.Loop, "Audio adapter: native loop requested for background");
        music.IsPaused = true;
        check(player.IsPaused && music.IsPaused, "Audio adapter: Musical Chairs can pause");
        music.IsPaused = false;
        check(!player.IsPaused, "Audio adapter: Musical Chairs can resume same player");
        music.SetVolume(0.5f);
        check(player.Volume == 0.5f, "Audio adapter: volume does not alter playback source");
        var source = player.Source;
        music.Stop(); music.Stop();
        check(source.Disposed && player.Source == null, "Audio adapter: stop immediately releases stream");
        check(player.Destroys == 1 && player.Clears == 1, "Audio adapter: repeated stop is harmless");
        music.IsPaused = true; music.SetVolume(9);
        check(!music.IsPaused && player.Volume == 0.5f, "Audio adapter: stale stopped handle cannot change player");

        var effect = SlNaExtensions.PlayAudio("effect.ogg", "effect.ogg", false, true, 20, 30, default);
        AudioPlayer effectPlayer = AudioPlayerPool.Last;
        check(!effectPlayer.Source.Loop, "Audio adapter: one-shot effects keep loop disabled");
        effectPlayer.Finish();
        effect.Stop();
        check(effectPlayer.Destroys == 1 && effectPlayer.Clears == 0, "Audio adapter: natural end destroys once without double pooling");
        var next = SlNaExtensions.PlayAudio("next.ogg", "next.ogg", true, false, 5, 5000, default);
        AudioPlayer nextPlayer = AudioPlayerPool.Last;
        effect.Stop();
        check(nextPlayer.Destroys == 0, "Audio adapter: old effect handle cannot stop next event");
        var personal = SlNaExtensions.PlayPlayerAudio(new Player { NetworkId = 77 }, "scream.ogg", "scream.ogg", false);
        AudioPlayer personalPlayer = AudioPlayerPool.Last;
        check(personalPlayer.Filter(new Player { NetworkId = 77 }) && !personalPlayer.Filter(new Player { NetworkId = 78 }), "Audio adapter: personal sounds retain recipient filter");
        var unrelated = new AudioPlayer();
        SlNaExtensions.StopAll(); SlNaExtensions.StopAll();
        check(nextPlayer.Destroys == 1 && personalPlayer.Destroys == 1, "Audio adapter: unload stops every owned source once");
        check(unrelated.Destroys == 0, "Audio adapter: unload preserves other plugins' sources");
        check(!next.IsPaused && !personal.IsPaused, "Audio adapter: disposed handles remain safe");

        AudioSourceFactory.Fail = true;
        bool failed = false;
        try { SlNaExtensions.PlayAudio("bad.ogg", "bad.ogg", true, false, 5, 5000, default); }
        catch (InvalidOperationException) { failed = true; }
        finally { AudioSourceFactory.Fail = false; }
        check(failed && AudioPlayerPool.Last.Destroys == 1, "Audio adapter: failed initialization destroys rented player");
        SlNaExtensions.StopAll();
    }
}

namespace AutoEvent.Integrations.Audio
{
    internal sealed class FakeAudioSource(bool loop) : IDisposable
    {
        internal bool Loop => loop;
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
    internal static class AudioSourceFactory
    {
        internal static bool Fail;
        internal static FakeAudioSource Open(string name, string path, bool loop)
        {
            if (Fail) throw new InvalidOperationException("Fake decoder error");
            return new FakeAudioSource(loop);
        }
    }
}
namespace SecretLabNAudio.Core
{
    public sealed class SpeakerSettings
    {
        public float Volume, MinDistance, MaxDistance;
        public bool IsSpatial;
    }
    public sealed class AudioPlayer
    {
        internal FakeAudioSource Source;
        internal Predicate<Player> Filter;
        internal int Destroys, Clears;
        internal float Volume;
        public bool IsPaused;
        public event Action Ended;
        public event Action Destroyed;
        public void ClearBuffer() => Clears++;
        public void Destroy()
        {
            Destroys++;
            Source?.Dispose();
            Source = null;
            Destroyed?.Invoke();
        }
        internal void Finish() => Ended?.Invoke();
    }
}
namespace SecretLabNAudio.Core.Pools
{
    public static class AudioPlayerPool
    {
        public static AudioPlayer Last;
        public static AudioPlayer Rent(SpeakerSettings settings, UnityEngine.Vector3 position) => Last = new AudioPlayer();
    }
}
namespace SecretLabNAudio.Core.Extensions
{
    public static class AudioPlayerExtensions
    {
        internal static AudioPlayer Use(this AudioPlayer p, FakeAudioSource source) { p.Source = source; return p; }
        public static AudioPlayer DestroyOnEnd(this AudioPlayer p) { p.Ended += p.Destroy; return p; }
        public static AudioPlayer WithFilteredSendEngine(this AudioPlayer p, Predicate<Player> filter) { p.Filter = filter; return p; }
        public static AudioPlayer WithoutProvider(this AudioPlayer p) { p.Source?.Dispose(); p.Source = null; return p; }
        public static void WithVolume(this AudioPlayer p, float volume) => p.Volume = volume;
    }
}
