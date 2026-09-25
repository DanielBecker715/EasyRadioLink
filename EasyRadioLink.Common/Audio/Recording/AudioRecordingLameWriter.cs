using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using EasyRadioLink.Common.Audio.NAudioLame;
using EasyRadioLink.Common.Settings;
using NAudio.Wave;
using NLog;

namespace EasyRadioLink.Common.Audio.Recording;

internal class AudioRecordingLameWriter : AudioRecordingWriterBase
{
    private readonly List<string> _mp3FilePaths;
    private readonly List<LameMP3FileWriter> _mp3FileWriters;

    // construct an audio file writer that uses LAME to encode the streams in an .mp3 file using
    // the specified sample rate. the streams list provides the audio streams that supply the
    // audio data, each stream is written to its own file (named per the current date and time
    // and the stream tag). the writer will process the specified maximum number of samples per
    // call to ProcessAudio().
    public AudioRecordingLameWriter(IReadOnlyList<AudioRecordingStream> streams, int sampleRate, int maxSamples)
        :base(streams, sampleRate, maxSamples)
    {
        _mp3FilePaths = new List<string>();
        _mp3FileWriters = new List<LameMP3FileWriter>();
    }

    // attempt to write up to the max samples from each stream to their output files. this will
    // start the writer if it is not currently started.
    protected override void DoPrepareProcessAudio()
    {
        if (_mp3FileWriters.Count == 0) Start();
    }


    protected override void DoProcessAudioStream(int streamIndex, ReadOnlySpan<float> samples)
    {
        _mp3FileWriters[streamIndex].Write(MemoryMarshal.AsBytes(samples));
    }

    public override void Start()
    {
        // streams are stored in the recordings folder, named "<date>_<time><tag>.mp3"
        var filePathBase = CreateFilePathBase(DateTime.Now);

        var lamePreset = (LAMEPreset)Enum.Parse(typeof(LAMEPreset),
            GlobalSettingsStore.Instance.GetClientSetting(GlobalSettingsKeys.RecordingQuality).RawValue);

        for (var i = 0; i < Streams.Count; i++)
        {
            var tag = Streams[i].Tag;
            if (tag == null || tag.Length == 0) tag = "";
            _mp3FilePaths.Add(filePathBase + tag + ".mp3");
            _mp3FileWriters.Add(new LameMP3FileWriter(_mp3FilePaths[i], WaveFormat, lamePreset));
        }
    }

    public override void Stop()
    {
        // this should be sufficient to get any left over samples into the files. might be
        // better to process until streams have no more data, but laziness intensifies...
        ProcessAudio();
        ProcessAudio();

        foreach (var writer in _mp3FileWriters)
        {
            writer.Flush();
            writer.Dispose();
        }

        _mp3FileWriters.Clear();
        _mp3FilePaths.Clear();
    }
}