using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiAudioRecoder.Models;
using Whisper.net;
using Whisper.net.Ggml;
using NAudio.Wave;

namespace AiAudioRecoder.Services
{
    public enum DeviceType
    {
        Cpu,
        Gpu,
    }

    public class WhisperTranscriptionService
    {
        public class TranscriptionOutput
        {
            public string Text { get; set; } = string.Empty;
            public List<TranscriptionWordTiming> WordTimings { get; set; } = new();
        }

        public string ModelPath { get; set; }
        public DeviceType Device { get; set; }
        public int DeviceIndex { get; set; }

        public WhisperTranscriptionService(
            string modelPath,
            DeviceType device = DeviceType.Cpu,
            int deviceIndex = 0
        )
        {
            ModelPath = modelPath;
            Device = device;
            DeviceIndex = deviceIndex;
        }

        public Task<TranscriptionOutput> TranscribeAsync(string audioFilePath)
        {
            return TranscribeInternalAsync(audioFilePath, CancellationToken.None);
        }

        public Task<TranscriptionOutput> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken)
            => TranscribeInternalAsync(audioFilePath, cancellationToken);

        private async Task<TranscriptionOutput> TranscribeInternalAsync(string audioFilePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(ModelPath))
            {
                System.Diagnostics.Debug.WriteLine($"Model not found: {ModelPath}. Skipping transcription.");
                return new TranscriptionOutput
                {
                    Text = "Транскрипция недоступна: модель не загружена.",
                    WordTimings = new List<TranscriptionWordTiming>()
                };
            }

            string? tempPath = null;
            try
            {
                tempPath = Path.GetTempFileName() + ".wav";
                using (var reader = new WaveFileReader(audioFilePath))
                {
                    IWaveProvider provider = reader;
                    if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
                    {
                        provider = new MediaFoundationResampler(provider, new WaveFormat(16000, 1));
                    }
                    using (var output = new WaveFileWriter(tempPath, new WaveFormat(16000, 1)))
                    {
                        var buffer = new float[4096];
                        int samplesRead;
                        while ((samplesRead = provider.ToSampleProvider().Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            output.WriteSamples(buffer, 0, samplesRead);
                        }
                    }
                }

                using var factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions()
                {
                    UseGpu = Device == DeviceType.Gpu,
                    GpuDevice = DeviceIndex
                });
                using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
                using var audioStream = File.OpenRead(tempPath);
                var textBuilder = new StringBuilder();
                var timings = new List<TranscriptionWordTiming>();
                await foreach (var result in processor.ProcessAsync(audioStream, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var segmentText = result.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(segmentText))
                    {
                        continue;
                    }

                    textBuilder.Append(segmentText);
                    textBuilder.Append(' ');

                    timings.AddRange(ConvertSegmentToWordTimings(result.Text, result.Start, result.End));
                }
                return new TranscriptionOutput
                {
                    Text = textBuilder.ToString().Trim(),
                    WordTimings = timings
                };
            }
            catch (OperationCanceledException)
            {
                return new TranscriptionOutput();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Transcription error: {ex.Message}");
                return new TranscriptionOutput
                {
                    Text = "Ошибка транскрипции.",
                    WordTimings = new List<TranscriptionWordTiming>()
                };
            }
            finally
            {
                if (tempPath != null && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        // Теперь сохраняем только время всего сегмента (без деления на слова).
        // Возвращаем один элемент на сегмент: Word = полный текст сегмента, Start/End = границы сегмента.
        private static IEnumerable<TranscriptionWordTiming> ConvertSegmentToWordTimings(string? text, TimeSpan start, TimeSpan end)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            double segmentStart = start.TotalSeconds;
            double segmentEnd = end.TotalSeconds;
            yield return new TranscriptionWordTiming
            {
                Word = text.Trim(),
                Start = Math.Max(0, segmentStart),
                End = Math.Max(segmentStart, segmentEnd)
            };
        }
    }
}
