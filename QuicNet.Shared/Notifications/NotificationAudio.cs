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

    public static byte[] Wave(string tone, int volume, string category = "system")
    {
        const int rate = 22050;
        int pulses = category is "messages" or "files" or "screen" ? 2 : 1;
        int samples = pulses == 2 ? rate * 3 / 5 : rate / 3;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        double frequency = tone == "bell" ? 880 : tone == "digital" ? 1100 : 660;
        for (int i = 0; i < samples; i++)
        {
            double time = (double)i / rate;
            bool second = pulses == 2 && time >= 0.28;
            double local = second ? time - 0.28 : time;
            double pitch = second ? category == "files" ? 1.5 : category == "screen" ? 0.75 : 1.25 : 1;
            double envelope = pulses == 2 && !second && time > 0.22 ? 0 : Math.Min(1, local * 100) * Math.Exp(-local * 12);
            writer.Write((short)(Math.Sin(2 * Math.PI * frequency * pitch * local) * envelope * 12000 * Math.Clamp(volume, 0, 100) / 100));
        }
        return stream.ToArray();
    }
}
