using System.IO;
using AutoEvent.API;
using SecretLabNAudio.Core.Extensions.Processors;
using SecretLabNAudio.Core.FileReading;
using SecretLabNAudio.Core.Processors;

namespace AutoEvent.Integrations.Audio;

internal static class AudioSourceFactory
{
    // Both sources use the same decoder, native loop and rate conversion; no duration or speed adjustment.
    internal static IAudioProcessor Open(string fileName, string filePath, bool loop)
    {
        Stream stream = File.Exists(filePath) ? File.OpenRead(filePath) : MusicManager.OpenEmbedded(fileName);
        if (stream == null) throw new FileNotFoundException("Audio source was not found.", fileName);
        StreamAudioProcessor processor = null;
        try
        {
            processor = CreateAudioProcessor.FromStream(stream, Path.GetExtension(fileName));
            return processor.WithLoop(loop).ToPlayerCompatible();
        }
        catch
        {
            if (processor != null) processor.Dispose();
            else stream.Dispose();
            throw;
        }
    }
}
