using System.Collections.Generic;
using System.Linq;
using AutoEvent.API;
using LabApi.Features.Wrappers;
using SecretLabNAudio.Core;
using SecretLabNAudio.Core.Extensions;
using SecretLabNAudio.Core.Pools;
using UnityEngine;

namespace AutoEvent.Integrations.Audio;

internal static class SlNaExtensions
{
    private static readonly HashSet<SlnaHandle> Active = [];

    public static IAudioHandle PlayAudio(string fileName, string filePath, bool isLoop, bool isSpatial, float minDistance, float maxDistance, Vector3 speakerPosition)
    {
        SpeakerSettings settings = new()
        {
            Volume = AutoEvent.MusicVolume / 100f,
            IsSpatial = isSpatial,
            MinDistance = minDistance,
            MaxDistance = maxDistance
        };

        return Start(AudioPlayerPool.Rent(settings, position: speakerPosition), fileName, filePath, isLoop);
    }

    public static IAudioHandle PlayPlayerAudio(Player labPlayer, string fileName, string filePath, bool isLoop)
    {
        SpeakerSettings settings = new()
        {
            Volume = AutoEvent.MusicVolume / 100f,
            IsSpatial = false,
            MaxDistance = 5000f
        };

        return Start(AudioPlayerPool.Rent(settings, position: labPlayer.Position).WithFilteredSendEngine(p => p.NetworkId == labPlayer.NetworkId), fileName, filePath, isLoop);
    }

    private static IAudioHandle Start(AudioPlayer player, string fileName, string filePath, bool isLoop)
    {
        try
        {
            player.Use(AudioSourceFactory.Open(fileName, filePath, isLoop)).DestroyOnEnd();
            return new SlnaHandle(player);
        }
        catch
        {
            player.Destroy();
            throw;
        }
    }

    internal static void StopAll()
    {
        foreach (SlnaHandle handle in Active.ToArray()) handle.Stop();
    }

    private sealed class SlnaHandle : IAudioHandle
    {
        private AudioPlayer _player;
        internal SlnaHandle(AudioPlayer player)
        {
            _player = player;
            Active.Add(this);
            player.Ended += Release;
            player.Destroyed += Release;
        }

        private void Release()
        {
            AudioPlayer previous = _player;
            _player = null;
            Active.Remove(this);
            if (previous == null) return;
            previous.Ended -= Release;
            previous.Destroyed -= Release;
        }

        public bool IsPaused
        {
            get => _player != null && _player.IsPaused;
            set
            {
                if (_player != null) _player.IsPaused = value;
            }
        }

        public void Stop()
        {
            AudioPlayer player = _player;
            Release();
            if (player == null) return;
            player.ClearBuffer();
            player.WithoutProvider();
            player.Destroy();
        }

        public void SetVolume(float volume)
        {
            if (_player != null) _player.WithVolume(volume);
        }
    }
}
