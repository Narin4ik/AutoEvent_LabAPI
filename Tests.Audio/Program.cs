using AutoEvent.API;
using AutoEvent.Integrations.Audio;
using NAudio.Vorbis;
using NAudio.Wave;
using SecretLabNAudio.Core.FileReading;
using SecretLabNAudio.Core.Processors;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception(message);
    }

    private static int Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AutoEvent-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (int rate in new[] { 44100, 48000 })
            foreach (int channels in new[] { 1, 2 })
            {
                string path = Path.Combine(folder, $"tone-{rate}-{channels}.wav");
                MakeTone(path, rate, channels);
                float[] single;
                using (IAudioProcessor source = AudioSourceFactory.Open(Path.GetFileName(path), path, false))
                {
                    Check(source.WaveFormat.SampleRate == 48000 && source.WaveFormat.Channels == 1, "Player-compatible format");
                    single = ReadAll(source);
                    Check(Math.Abs(single.Length - 12000) <= 96, $"Original 0.25-second duration at {rate}Hz/{channels}ch");
                    Check(Math.Abs(Frequency(single, 512, single.Length - 1024) - 440) < 6, "Original pitch");
                }
                using (IAudioProcessor source = AudioSourceFactory.Open(Path.GetFileName(path), path, true))
                {
                    float[] samples = ReadExactly(source, 3 * 12000);
                    Check(samples.Length == 36000, "Native looping supplies three complete cycles");
                    for (int cycle = 0; cycle < 3; cycle++)
                    {
                        Check(Math.Abs(Frequency(samples, cycle * 12000 + 512, 10976) - 440) < 6, "Pitch unchanged on every loop");
                        double difference = RmsDifference(single, 512, samples, cycle * 12000 + 512, 10976);
                        Console.WriteLine($"{rate}Hz/{channels}ch cycle {cycle + 1}: frames={single.Length}, rms-error={difference:F6}");
                        Check(difference < 0.002, "Each loop has original waveform, duration and amplitude");
                    }
                }
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(true, "Disposed processor releases user file");
                Console.WriteLine($"WAV {rate}Hz/{channels}ch: 0.25s, 440Hz, three loops preserved.");
            }

            AudioReaderFactoryManager.RegisterFactory("ogg", new VorbisFactory());
            const string embeddedName = "human_death_01.ogg";
            string oggPath = Path.Combine(folder, embeddedName);
            using (Stream embedded = MusicManager.OpenEmbedded(embeddedName))
            using (FileStream file = File.Create(oggPath)) embedded.CopyTo(file);
            float[] baseline;
            using (IAudioProcessor source = AudioSourceFactory.Open(embeddedName, oggPath, false)) baseline = ReadAll(source);
            using (IAudioProcessor source = AudioSourceFactory.Open(embeddedName, "absent.ogg", false))
            {
                float[] embedded = ReadAll(source);
                Check(embedded.Length == baseline.Length && RmsDifference(baseline, 0, embedded, 0, baseline.Length) < 0.000001,
                    "Actual embedded OGG and disk override decode identically");
            }
            int period;
            using (var reader = new VorbisWaveReader(oggPath)) period = (int)Math.Round(reader.TotalTime.TotalSeconds * 48000);
            foreach (string path in new[] { oggPath, "absent.ogg" })
            using (IAudioProcessor source = AudioSourceFactory.Open(embeddedName, path, true))
            {
                float[] samples = ReadExactly(source, 3 * period);
                Check(samples.Length == 3 * period, "Actual OGG repeats for three cycles");
                int count = Math.Min(period, baseline.Length) - 1024;
                for (int cycle = 0; cycle < 3; cycle++)
                    Check(RmsDifference(baseline, 512, samples, cycle * period + 512, count) < 0.003,
                        "Embedded/user OGG repeats without speed change or overlapping copies");
            }
            Console.WriteLine($"Embedded and disk OGG: {period / 48000d:F3}s per cycle, three loops preserved.");

            bool missing = false;
            try { AudioSourceFactory.Open("missing.wav", Path.Combine(folder, "missing.wav"), true); }
            catch (FileNotFoundException) { missing = true; }
            Check(missing, "Missing source fails without creating a player");
            string invalidPath = Path.Combine(folder, "invalid.unsupported");
            File.WriteAllBytes(invalidPath, new byte[10]);
            bool unsupported = false;
            try { AudioSourceFactory.Open("invalid.unsupported", invalidPath, true); }
            catch (NotSupportedException) { unsupported = true; }
            Check(unsupported, "Unsupported source is rejected");
            using (File.Open(invalidPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(true, "Failed decoder creation releases file");
            Console.WriteLine($"Passed {_checks} audio decoding, duration, pitch and loop checks.");
            return 0;
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void MakeTone(string path, int rate, int channels)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        for (int frame = 0; frame < rate / 4; frame++)
            for (int channel = 0; channel < channels; channel++)
                writer.WriteSample((float)(0.25 * Math.Sin(2 * Math.PI * 440 * frame / rate)));
    }

    private static float[] ReadAll(IAudioProcessor source)
    {
        var result = new List<float>();
        var buffer = new float[997]; // Deliberately crosses loop boundaries.
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.AddRange(buffer.Take(count));
            if (result.Count > 10000000) throw new Exception("One-shot source did not end");
        }
        return result.ToArray();
    }

    private static float[] ReadExactly(IAudioProcessor source, int length)
    {
        var result = new float[length];
        var buffer = new float[997];
        int total = 0;
        while (total < length)
        {
            int read = source.Read(buffer, 0, Math.Min(buffer.Length, length - total));
            if (read == 0) throw new Exception("Loop ended early");
            Array.Copy(buffer, 0, result, total, read);
            total += read;
        }
        return result;
    }

    private static double Frequency(float[] samples, int offset, int count)
    {
        int crossings = 0;
        for (int i = offset + 1; i < offset + count; i++) if (samples[i - 1] <= 0 && samples[i] > 0) crossings++;
        return crossings * 48000d / count;
    }

    private static double RmsDifference(float[] a, int aOffset, float[] b, int bOffset, int count)
    {
        if (count <= 0) throw new Exception("Audio fixture is too short");
        double sum = 0;
        for (int i = 0; i < count; i++) { double d = a[aOffset + i] - b[bOffset + i]; sum += d * d; }
        return Math.Sqrt(sum / count);
    }

    private sealed class VorbisFactory : IAudioReaderFactory
    {
        public AudioReaderFactoryResult FromPath(string path) { var reader = new VorbisWaveReader(path); return new(reader, reader); }
        public AudioReaderFactoryResult FromStream(Stream stream, bool closeOnDispose) { var reader = new VorbisWaveReader(stream, closeOnDispose); return new(reader, reader); }
    }
}

namespace AutoEvent.ApiFeatures
{
    internal static class LogManager
    {
        internal static void Warn(string message) => Console.WriteLine(message);
        internal static void Info(string message) => Console.WriteLine(message);
    }
}
