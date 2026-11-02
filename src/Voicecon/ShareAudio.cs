using System.Runtime.InteropServices;
using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;
namespace Voicecon;
// WASAPI process loopback. Created only after the user explicitly enables sharing sound.
public sealed class ProcessLoopback : IDisposable
{
    public uint StreamId { get; } = (uint)Random.Shared.Next(1, int.MaxValue);
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    private readonly CancellationTokenSource stop = new(); private Task? worker; private int disposed;
    public event Action<byte[]>? Packet; public event Action<string>? Failed;
    public async Task StartAsync(int processId, bool include)
    {
        if (!Supported) throw new NotSupportedException("Звук приложения требует Windows 11 / сборку 20348+.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = Task.Run(async () =>
        {
            IAudioClient? client = null; ICaptureClient? capture = null;
            try
            {
                var handler = new Activation(); var iid = typeof(IAudioClient).GUID;
                var data = Marshal.AllocHGlobal(12); var variant = new PROPVARIANT { Type = 65, Size = 12, Data = data };
                Marshal.WriteInt32(data, 0, 1); Marshal.WriteInt32(data, 4, processId); Marshal.WriteInt32(data, 8, include ? 0 : 1);
                try
                {
                    Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref variant, handler, out var op));
                    try { client = await handler.Result.Task.WaitAsync(TimeSpan.FromSeconds(8), stop.Token); } finally { Marshal.ReleaseComObject(op); }
                }
                finally { Marshal.FreeHGlobal(data); }
                var format = new WAVEFORMAT { Tag = 1, Channels = 2, Rate = 48000, Average = 192000, Align = 4, Bits = 16 };
                Marshal.ThrowExceptionForHR(client.Initialize(0, 0x80060000, 0, 0, ref format, IntPtr.Zero));
                using var signal = new AutoResetEvent(false); Marshal.ThrowExceptionForHR(client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle()));
                var captureId = typeof(ICaptureClient).GUID; Marshal.ThrowExceptionForHR(client.GetService(ref captureId, out var service)); capture = (ICaptureClient)service;
                var encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO); encoder.Bitrate = 64000; encoder.Complexity = 3; encoder.UseDTX = true;
                var frame = new byte[3840]; var encoded = new byte[1275]; var fill = 0; ushort sequence = 0;
                Marshal.ThrowExceptionForHR(client.Start()); ready.TrySetResult();
                var waits = new[] { signal, stop.Token.WaitHandle };
                while (WaitHandle.WaitAny(waits, 500) != 1 && !stop.IsCancellationRequested)
                {
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var length));
                    while (length > 0)
                    {
                        Marshal.ThrowExceptionForHR(capture.GetBuffer(out var pointer, out var count, out var flags, out _, out _));
                        try
                        {
                            var bytes = new byte[checked((int)count * 4)]; if ((flags & 2) == 0) Marshal.Copy(pointer, bytes, 0, bytes.Length);
                            var offset = 0;
                            while (offset < bytes.Length)
                            {
                                var take = Math.Min(bytes.Length - offset, frame.Length - fill); Buffer.BlockCopy(bytes, offset, frame, fill, take); fill += take; offset += take;
                                if (fill != frame.Length) continue;
                                var size = encoder.Encode(MemoryMarshal.Cast<byte, short>(frame), 960, encoded, 1275); var packet = new byte[size + 6]; BinaryPrimitives.WriteUInt32LittleEndian(packet, StreamId); BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), sequence++); encoded.AsSpan(0, size).CopyTo(packet.AsSpan(6)); Packet?.Invoke(packet); fill = 0;
                            }
                        }
                        finally { capture.ReleaseBuffer(count); }
                        Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out length));
                    }
                }
            }
            catch (Exception ex) { ready.TrySetException(ex); if (!stop.IsCancellationRequested) Failed?.Invoke("Звук демонстрации: " + ex.Message); }
            finally { if (client is not null) { client.Stop(); Marshal.ReleaseComObject(client); } if (capture is not null) Marshal.ReleaseComObject(capture); }
        });
        await ready.Task;
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; stop.Cancel(); if (worker is { } task) _ = task.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default); else stop.Dispose(); }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct PROPVARIANT { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public int Size; [FieldOffset(16)] public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] public struct WAVEFORMAT { public ushort Tag, Channels; public uint Rate, Average; public ushort Align, Bits, Extra; }
    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode)] private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, ref PROPVARIANT parameters, ICompletion handler, out IOperation operation);
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface IOperation { [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated); }
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface ICompletion { [PreserveSig] int ActivateCompleted(IOperation operation); }
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface IAgile { }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)] public sealed class Activation : ICompletion, IAgile
    {
        public readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IOperation operation)
        { try { Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var result, out var obj)); Marshal.ThrowExceptionForHR(result); Result.TrySetResult((IAudioClient)obj); } catch (Exception ex) { Result.TrySetException(ex); } return 0; }
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface IAudioClient
    {
        [PreserveSig] int Initialize(int share, uint flags, long duration, long periodicity, ref WAVEFORMAT format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames); [PreserveSig] int GetStreamLatency(out long latency); [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int share, IntPtr format, out IntPtr closest); [PreserveSig] int GetMixFormat(out IntPtr format); [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset(); [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface ICaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong time);
        [PreserveSig] int ReleaseBuffer(uint frames); [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
public sealed class ShareAudioPlayer : IDisposable
{
    private readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 2);
    private readonly BufferedWaveProvider buffer = new(new WaveFormat(48000, 16, 2)) { BufferDuration = TimeSpan.FromMilliseconds(180), DiscardOnBufferOverflow = true };
    private readonly WaveOutEvent output; private readonly object gate = new(); private bool disposed, haveSequence; private ushort last;
    public bool Deafened; public float Volume = 1;
    public ShareAudioPlayer(int device) { output = new WaveOutEvent { DeviceNumber = device, DesiredLatency = 60, NumberOfBuffers = 3 }; output.Init(new OutputProvider(this)); output.Play(); }
    public void Add(byte[] data)
    {
        if (data.Length is < 7 or > 1281) return;
        lock (gate)
        {
            if (disposed) return; var sequence = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)); if (haveSequence && (short)(sequence - last) <= 0) return; haveSequence = true; last = sequence;
            var samples = new short[1920 * 6]; int count;
            try { count = decoder.Decode(data.AsSpan(6), samples, 5760, false) * 2; } catch (Exception) { return; }
            if (Deafened) { buffer.ClearBuffer(); return; }
            if (buffer.BufferedDuration.TotalMilliseconds > 120) buffer.ClearBuffer(); var bytes = MemoryMarshal.AsBytes(samples.AsSpan(0, count)).ToArray(); buffer.AddSamples(bytes, 0, bytes.Length);
        }
    }
    public void Dispose() { lock (gate) { disposed = true; output.Dispose(); } }
    private sealed class OutputProvider(ShareAudioPlayer owner) : IWaveProvider
    {
        public WaveFormat WaveFormat => owner.buffer.WaveFormat;
        public int Read(byte[] data, int offset, int count)
        {
            var read = owner.buffer.Read(data, offset, count); var samples = MemoryMarshal.Cast<byte, short>(data.AsSpan(offset, read));
            if (owner.Deafened) samples.Clear();
            else for (var i = 0; i < samples.Length; i++) samples[i] = AudioLevels.Limit(samples[i] * owner.Volume);
            return read;
        }
    }
}
