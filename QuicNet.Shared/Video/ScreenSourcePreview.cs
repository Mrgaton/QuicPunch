namespace QuicNet.Video;

internal sealed partial class FfmpegScreenEncoder
{
    private static readonly SemaphoreSlim PreviewSlots = new(2, 2);

    public static async Task<byte[]> PreviewAsync(string source, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await PreviewSlots.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var options = new ScreenEncoderOptions { Source = source, Width = 480, Height = 270, Fps = 5, Encoder = "libx264" };
            // Use precisely the same input rectangle as the transmission, then
            // scale the entire frame to a thumbnail without cropping it.
            var args = CaptureArguments(options, source == "dxgi:0" ? "h264_nvenc" : "libx264");
            args.RemoveRange(args.IndexOf("-an"), args.Count - args.IndexOf("-an"));
            if (source == "dxgi:0") args[args.IndexOf("-vf") + 1] = "hwdownload,format=bgra," + ScaleFilter(options);
            args.AddRange(["-frames:v", "1", "-an", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2pipe", "pipe:1"]);
            using var process = StartProcess(args);
            try
            {
                var errors = process.StandardError.ReadToEndAsync(deadline.Token);
                using var image = new MemoryStream();
                byte[] buffer = new byte[16384];
                int count;
                while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (image.Length + count > 512 * 1024) throw new IOException("La previsualización excede el tamaño permitido.");
                    image.Write(buffer, 0, count);
                }
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                if (process.ExitCode != 0 || image.Length == 0)
                    throw new IOException("No se pudo previsualizar la fuente: " + Tail(await errors.ConfigureAwait(false)));
                return image.ToArray();
            }
            finally { Kill(process); }
        }
        finally { PreviewSlots.Release(); }
    }
}

