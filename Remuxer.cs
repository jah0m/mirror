using System.Runtime.InteropServices;

namespace Mirror;

/// <summary>
/// Combines a video-only and an audio-only file into one MP4 with Media Foundation.
/// Compressed samples are copied as they are, so this takes moments and loses no quality.
/// </summary>
internal static class Remuxer
{
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint EndOfStreamFlag = 0x2;

    /// <param name="audioLeadTicks">How long (100 ns units) the audio started before the video.</param>
    public static void Combine(string videoPath, string audioPath, long audioLeadTicks, string outputPath)
    {
        MF.Check(MF.MFStartup(MF.Version, 0));
        IMFSourceReader? video = null, audio = null;
        IMFSinkWriter? writer = null;
        try
        {
            MF.Check(MF.MFCreateSourceReaderFromURL(videoPath, IntPtr.Zero, out video));
            MF.Check(MF.MFCreateSourceReaderFromURL(audioPath, IntPtr.Zero, out audio));
            MF.Check(MF.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, IntPtr.Zero, out writer));

            uint videoOut = AddStream(writer, video, FirstVideoStream);
            uint audioOut = AddStream(writer, audio, FirstAudioStream);
            writer.BeginWriting();

            var nextVideo = Read(video, FirstVideoStream);
            var nextAudio = ReadAudio(audio, audioLeadTicks);

            // Write in time order so the muxer can interleave the tracks evenly.
            while (nextVideo.Sample is not null || nextAudio.Sample is not null)
            {
                bool takeVideo = nextAudio.Sample is null
                                 || (nextVideo.Sample is not null && nextVideo.Time <= nextAudio.Time);
                if (takeVideo)
                {
                    writer.WriteSample(videoOut, nextVideo.Sample!);
                    Marshal.ReleaseComObject(nextVideo.Sample!);
                    nextVideo = Read(video, FirstVideoStream);
                }
                else
                {
                    nextAudio.Sample!.SetSampleTime(nextAudio.Time);
                    writer.WriteSample(audioOut, nextAudio.Sample);
                    Marshal.ReleaseComObject(nextAudio.Sample);
                    nextAudio = ReadAudio(audio, audioLeadTicks);
                }
            }

            writer.Finish();
        }
        finally
        {
            if (writer is not null) Marshal.ReleaseComObject(writer);
            if (audio is not null) Marshal.ReleaseComObject(audio);
            if (video is not null) Marshal.ReleaseComObject(video);
            MF.MFShutdown();
        }
    }

    private static uint AddStream(IMFSinkWriter writer, IMFSourceReader reader, uint stream)
    {
        reader.GetCurrentMediaType(stream, out IntPtr type);
        try
        {
            writer.AddStream(type, out uint index);
            writer.SetInputMediaType(index, type, IntPtr.Zero);
            return index;
        }
        finally
        {
            Marshal.Release(type);
        }
    }

    private static (IMFSample? Sample, long Time) Read(IMFSourceReader reader, uint stream)
    {
        while (true)
        {
            reader.ReadSample(stream, 0, out _, out uint flags, out long time, out IMFSample? sample);
            if (sample is not null)
                return (sample, time);
            if ((flags & EndOfStreamFlag) != 0)
                return (null, 0);
        }
    }

    private static (IMFSample? Sample, long Time) ReadAudio(IMFSourceReader reader, long leadTicks)
    {
        while (true)
        {
            var (sample, time) = Read(reader, FirstAudioStream);
            if (sample is null)
                return (null, 0);
            long shifted = time - leadTicks;
            if (shifted >= 0)
                return (sample, shifted);
            Marshal.ReleaseComObject(sample); // Sound from before the video began.
        }
    }
}
