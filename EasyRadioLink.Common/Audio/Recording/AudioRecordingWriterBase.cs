using EasyRadioLink.Common.Helpers;
using NAudio.Wave;
using NLog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EasyRadioLink.Common.Audio.Recording
{
    internal abstract class AudioRecordingWriterBase
    {
        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly IReadOnlyList<AudioRecordingStream> _streams;
        private readonly WaveFormat _waveFormat;
        private readonly int _maxSamples;
        private readonly int _sampleRate;

        protected IReadOnlyList<AudioRecordingStream> Streams => _streams;
        protected WaveFormat WaveFormat => _waveFormat;
        protected int MaxSamples => _maxSamples;
        protected int SampleRate => _sampleRate;

        protected AudioRecordingWriterBase(IReadOnlyList<AudioRecordingStream> streams, int sampleRate, int maxSamples)
        {
            _streams = streams;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            _sampleRate = sampleRate;
            _maxSamples = maxSamples;
        }

        /// <summary>Folder the recordings are written to (<see cref="AppPaths.RecordingsDirectory" />, created on access).</summary>
        public static string RecordingsFolder => AppPaths.RecordingsDirectory;

        /// <summary>
        ///     Path of a new recording without tag and extension: <c>&lt;RecordingsFolder&gt;\yyyy-MM-dd_HH-mm-ss</c>
        ///     (local time, culture independent so files sort by date).
        /// </summary>
        protected static string CreateFilePathBase(DateTime localTime)
        {
            return Path.Combine(RecordingsFolder,
                localTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
        }

        public void ProcessAudio()
        {
            DoPrepareProcessAudio();
            try
            {
                // read from each of the streams and write the results to the file associated with
                // the stream. note that a "stream" here can be a mix of multiple audio streams.
                var floatPool = ArrayPool<float>.Shared;
                var streamBuffer = floatPool.Rent(MaxSamples);
                for (var i = 0; i < Streams.Count; i++)
                {
                    var samplesRead = Streams[i].Read(streamBuffer, MaxSamples);

                    DoProcessAudioStream(i, streamBuffer.AsSpan(0, samplesRead));
                }

                floatPool.Return(streamBuffer);
            }
            catch (Exception ex)
            {
                _logger.Error($"Unable to write audio samples to output file: {ex.Message}");
            }
        }
        public abstract void Start();
        public abstract void Stop();

        protected abstract void DoPrepareProcessAudio();
        protected abstract void DoProcessAudioStream(int streamIndex, ReadOnlySpan<float> samples);
    }
}