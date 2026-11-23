using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PulseDesk.Agent;

/// <summary>
/// Captures the audio the employee HEARS (system/loopback - speakers, Teams/Zoom, YouTube)
/// and converts it to 16 kHz mono 16-bit PCM. Nothing is recorded to disk; chunks are handed
/// to the caller, which sends them over the already-encrypted agent WebSocket as text frames.
/// The browser can play 16 kHz mono PCM directly with the Web Audio API, so no codec/server
/// changes are needed and the existing live-screen connection is untouched.
/// </summary>
internal sealed class AudioCapturer : IDisposable
{
    public const int TargetRate = 16000;

    private WasapiLoopbackCapture? _cap;
    private MMDevice? _device;   // keep the COM device alive for the capture's lifetime
    private readonly object _gate = new();
    private readonly MemoryStream _pending = new(16 * 1024);
    private long _lastFlush;
    private Action<byte[]>? _onChunk;
    private volatile bool _running;

    public bool IsRunning => _running;

    /// <summary>Starts loopback capture. Returns a short description of the source format.</summary>
    public string Start(Action<byte[]> onChunk)
    {
        Stop();
        // Capture the endpoint the employee actually HEARS. Windows keeps separate defaults for
        // Multimedia and Communications; Teams/Zoom often use the communications device (a
        // Bluetooth headset), so skipping "headset" endpoints captured silence on some laptops.
        // Prefer the multimedia default, then communications, then any active render device.
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? chosen = null;
        try { chosen = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
        if (chosen == null || chosen.State != DeviceState.Active)
        {
            try { chosen = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications); } catch { }
        }
        if (chosen == null || chosen.State != DeviceState.Active)
        {
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            if (devices.Count == 0) throw new InvalidOperationException("no active playback device");
            chosen = devices[0];
        }
        _device = chosen;
        var cap = new WasapiLoopbackCapture(chosen);
        var format = cap.WaveFormat;
        _onChunk = onChunk;
        _lastFlush = Environment.TickCount64;

        cap.DataAvailable += (_, e) =>
        {
            var pcm = Convert(e.Buffer, e.BytesRecorded, format);
            if (pcm.Length == 0) return;
            bool flush;
            lock (_gate)
            {
                _pending.Write(pcm, 0, pcm.Length);
                // ~10 chunks/second: small enough for low latency, large enough not to
                // flood the tunnel with tiny text frames.
                flush = Environment.TickCount64 - _lastFlush >= 100 || _pending.Length >= 6400;
                if (flush) _lastFlush = Environment.TickCount64;
            }
            if (flush) Flush();
        };

        cap.StartRecording();
        _cap = cap;
        _running = true;
        return $"{chosen.FriendlyName}: {format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit {format.Encoding}";
    }

    /// <summary>Converts the device format to 16 kHz mono 16-bit PCM (simple, dependency-free resample).</summary>
    private static byte[] Convert(byte[] buffer, int bytes, WaveFormat format)
    {
        int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        int channels = Math.Max(1, format.Channels);
        int frame = bytesPerSample * channels;
        int count = bytes / frame;
        if (count <= 0) return Array.Empty<byte>();

        // 48000 -> 16000 = keep every 3rd frame. 44100 -> 16000 keeps every 2nd/3rd
        // frame in turn, which is plenty for intelligible speech in a support call.
        int step = Math.Max(1, format.SampleRate / TargetRate);
        int outCount = (count + step - 1) / step;
        var pcm = new byte[outCount * 2];
        bool ieeeFloat = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        int oi = 0;
        for (int i = 0; i < count; i += step)
        {
            float sum = 0;
            int off = i * frame;
            for (int c = 0; c < channels; c++)
            {
                int p = off + c * bytesPerSample;
                if (p + bytesPerSample > bytes) break;
                float v;
                if (ieeeFloat) v = BitConverter.ToSingle(buffer, p);
                else if (format.BitsPerSample == 16) v = BitConverter.ToInt16(buffer, p) / 32768f;
                else if (format.BitsPerSample == 24)
                {
                    int raw = buffer[p] | (buffer[p + 1] << 8) | (buffer[p + 2] << 16);
                    if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
                    v = raw / 8388608f;
                }
                else v = buffer[p] / 128f - 1f;
                sum += v;
            }
            sum /= channels;
            if (sum > 1f) sum = 1f; else if (sum < -1f) sum = -1f;
            short sample = (short)(sum * short.MaxValue);
            pcm[oi++] = (byte)(sample & 0xff);
            pcm[oi++] = (byte)(sample >> 8);
        }
        return pcm;
    }

    private void Flush()
    {
        byte[] chunk;
        lock (_gate)
        {
            if (_pending.Length == 0) return;
            chunk = _pending.ToArray();
            _pending.SetLength(0);
        }
        try { _onChunk?.Invoke(chunk); } catch { }
    }

    public void Stop()
    {
        _running = false;
        var cap = _cap;
        _cap = null;
        if (cap != null)
        {
            try { cap.StopRecording(); } catch { }
            try { cap.Dispose(); } catch { }
        }
        try { _device?.Dispose(); } catch { }
        _device = null;
        Flush();
        _onChunk = null;
    }

    public void Dispose() => Stop();
}
