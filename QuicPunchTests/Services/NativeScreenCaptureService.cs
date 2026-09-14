using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QuicPunch;

namespace QuicPunchTests.Services;

[SupportedOSPlatform("windows")]
internal sealed class NativeScreenCaptureService : IDisposable
{
    private readonly Func<byte[], Task> _onFrameReady;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private int _isRunning;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public NativeScreenCaptureService(Func<byte[], Task> onFrameReady)
    {
        _onFrameReady = onFrameReady ?? throw new ArgumentNullException(nameof(onFrameReady));
    }

    public bool Start(int targetFps = 60, int maxWidth = 1920, int maxHeight = 1080, long jpegQuality = 60L)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return false;
        }

        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => CaptureLoopAsync(_workerCts.Token, targetFps, maxWidth, maxHeight, jpegQuality));
        QuicPunchLog.Info($"[SCREEN SHARE] Native host screen capture started at target {targetFps} FPS ({maxWidth}x{maxHeight}).");
        return true;
    }

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 0, 1) != 1)
        {
            return;
        }

        try { _workerCts?.Cancel(); } catch { }
        try { _workerTask?.Wait(1000); } catch { }
        try { _workerCts?.Dispose(); } catch { }
        _workerCts = null;
        _workerTask = null;
        QuicPunchLog.Info("[SCREEN SHARE] Native host screen capture stopped.");
    }

    private async Task CaptureLoopAsync(CancellationToken ct, int targetFps, int maxWidth, int maxHeight, long jpegQuality)
    {
        int frameIntervalMs = Math.Max(16, 1000 / Math.Clamp(targetFps, 5, 60));

        ImageCodecInfo? jpegEncoder = GetEncoder(ImageFormat.Jpeg);
        using EncoderParameters encoderParams = new(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, jpegQuality);

        using MemoryStream ms = new(64 * 1024);

        while (!ct.IsCancellationRequested)
        {
            long startTick = Environment.TickCount64;
            try
            {
                int screenWidth = GetSystemMetrics(0);  // SM_CXSCREEN
                int screenHeight = GetSystemMetrics(1); // SM_CYSCREEN

                if (screenWidth > 0 && screenHeight > 0)
                {
                    double scale = Math.Min(1.0, Math.Min((double)maxWidth / screenWidth, (double)maxHeight / screenHeight));
                    int destWidth = (int)(screenWidth * scale);
                    int destHeight = (int)(screenHeight * scale);

                    using Bitmap rawBmp = new(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
                    using (Graphics gRaw = Graphics.FromImage(rawBmp))
                    {
                        gRaw.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);
                    }

                    Bitmap targetBmp;
                    bool shouldDisposeTarget = false;

                    if (destWidth != screenWidth || destHeight != screenHeight)
                    {
                        targetBmp = new Bitmap(destWidth, destHeight, PixelFormat.Format24bppRgb);
                        shouldDisposeTarget = true;
                        using (Graphics gScaled = Graphics.FromImage(targetBmp))
                        {
                            gScaled.InterpolationMode = InterpolationMode.Bilinear;
                            gScaled.DrawImage(rawBmp, 0, 0, destWidth, destHeight);
                        }
                    }
                    else
                    {
                        targetBmp = rawBmp;
                    }

                    try
                    {
                        ms.SetLength(0);
                        if (jpegEncoder != null)
                        {
                            targetBmp.Save(ms, jpegEncoder, encoderParams);
                        }
                        else
                        {
                            targetBmp.Save(ms, ImageFormat.Jpeg);
                        }

                        byte[] jpegFrame = ms.ToArray();
                        if (jpegFrame.Length > 0)
                        {
                            await _onFrameReady(jpegFrame).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (shouldDisposeTarget)
                        {
                            targetBmp.Dispose();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[SCREEN SHARE] Native capture error: {ex.Message}");
            }

            long elapsed = Environment.TickCount64 - startTick;
            int delay = (int)Math.Max(10, frameIntervalMs - elapsed);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
        foreach (ImageCodecInfo codec in codecs)
        {
            if (codec.FormatID == format.Guid)
            {
                return codec;
            }
        }
        return null;
    }

    public void Dispose()
    {
        Stop();
    }
}
