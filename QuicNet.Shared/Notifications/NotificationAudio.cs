namespace QuicNet.Notifications;

public static class NotificationAudio
{
    public static byte[] Ringtone(int volume)
    {
        const int rate = 22050, samples = rate * 13 / 10;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            double time = (double)i / rate;
            double pulse = time < 0.46 ? time : time >= 0.68 && time < 1.14 ? time - 0.68 : -1;
            double envelope = pulse < 0 ? 0 : Math.Min(1, pulse / 0.015) * Math.Min(1, (0.46 - pulse) / 0.04);
            double tremolo = 0.8 + 0.2 * Math.Cos(2 * Math.PI * 12 * time);
            double chord = 0.6 * Math.Sin(2 * Math.PI * 440 * time) + 0.4 * Math.Sin(2 * Math.PI * 660 * time);
            writer.Write((short)(chord * envelope * tremolo * 18000 * Math.Clamp(volume, 0, 100) / 100));
        }
        return stream.ToArray();
    }

    public static byte[] Wave(string tone, int volume)
    {
        const int rate = 22050, samples = rate / 3;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        double frequency = tone == "bell" ? 880 : tone == "digital" ? 1100 : 660;
        for (int i = 0; i < samples; i++)
        {
            double time = (double)i / rate, envelope = Math.Min(1, i / 220.0) * Math.Exp(-time * 12);
            writer.Write((short)(Math.Sin(2 * Math.PI * frequency * time) * envelope * 12000 * Math.Clamp(volume, 0, 100) / 100));
        }
        return stream.ToArray();
    }
}
