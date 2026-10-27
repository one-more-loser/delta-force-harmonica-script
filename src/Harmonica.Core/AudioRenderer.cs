namespace Harmonica.Core;
public static class AudioRenderer
{
    public static void Render(Compilation plan, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        const int sampleRate = 22050; int samples = checked((int)Math.Ceiling(plan.DurationMs * sampleRate / 1000));
        using var file = File.Create(path); using var w = new BinaryWriter(file);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
        int noteIndex = 0;
        for (int i = 0; i < samples; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            double ms = i * 1000d / sampleRate;
            while (noteIndex < plan.Notes.Count && ms >= plan.Notes[noteIndex].StartMs + plan.Notes[noteIndex].DurationMs) noteIndex++;
            double value = 0;
            if (noteIndex < plan.Notes.Count)
            {
                var n = plan.Notes[noteIndex]; double position = ms - n.StartMs;
                if (position >= 0 && position < n.DurationMs)
                {
                    double frequency = 440 * Math.Pow(2, (n.Pitch - 69) / 12d);
                    double phase = 2 * Math.PI * frequency * position / 1000;
                    double envelope = Math.Min(1, position / 12) * Math.Min(1, (n.DurationMs - position) / 35);
                    value = (Math.Sin(phase) + 0.30 * Math.Sin(2 * phase) + 0.13 * Math.Sin(3 * phase)) * envelope * 0.42;
                }
            }
            w.Write((short)(Math.Clamp(value, -1, 1) * short.MaxValue));
        }
    }
}
