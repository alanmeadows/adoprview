using System.IO;
using System.Media;
using System.Text;

namespace AdoPrView;

internal sealed class HumanAttentionSound : IDisposable
{
    readonly MemoryStream _wave = new(CreateWave());
    readonly SoundPlayer _player;

    public HumanAttentionSound()
    {
        _player = new SoundPlayer(_wave);
        _player.Load();
    }

    public void Play() => _player.Play();

    internal static byte[] CreateWave()
    {
        const int rate = 22050;
        const double noteSeconds = 0.14, gapSeconds = 0.035, tailSeconds = 0.22;
        double[] notes = [659.255, 987.767, 830.609, 1244.508];
        var frames = (int)(rate * (notes.Length * (noteSeconds + gapSeconds) + tailSeconds));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + frames * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(frames * 2);
        for (var i = 0; i < frames; i++)
        {
            var time = (double)i / rate;
            double sample = 0;
            for (var n = 0; n < notes.Length; n++)
            {
                var t = time - n * (noteSeconds + gapSeconds);
                var duration = noteSeconds + (n == notes.Length - 1 ? tailSeconds : 0);
                if (t < 0 || t >= duration) continue;
                var envelope = Math.Min(1, t / 0.008) * Math.Pow(1 - t / duration, 2);
                var phase = 2 * Math.PI * notes[n] * t;
                sample += 0.18 * envelope * (Math.Sin(phase) + 0.18 * Math.Sin(2 * phase));
            }
            writer.Write((short)(Math.Clamp(sample, -1, 1) * short.MaxValue));
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        _player.Dispose();
        _wave.Dispose();
    }
}
