using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AiAudioRecoder.Models;
using AiAudioRecoder.Services;
using System.IO;

namespace AiAudioRecoder.Services
{
    public class TranscriptionQueueService
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly ConcurrentQueue<TranscriptionTask> _queue;
        private readonly ILogger<TranscriptionQueueService> _logger;
        private readonly WhisperTranscriptionService _whisperService;
        private readonly AudioMetadataDatabase _db;
        private bool _isProcessing = false;

        public TranscriptionQueueService(ILogger<TranscriptionQueueService> logger, 
            WhisperTranscriptionService whisperService, 
            AudioMetadataDatabase db)
        {
            _semaphore = new SemaphoreSlim(3, 3); // Max 3 concurrent transcriptions
            _queue = new ConcurrentQueue<TranscriptionTask>();
            _logger = logger;
            _whisperService = whisperService;
            _db = db;

            // Start processing queue
            _ = Task.Run(ProcessQueueAsync);
        }

        public async Task<TranscriptionResult> QueueTranscriptionAsync(AudioFileMetadata metadata)
        {
            var task = new TranscriptionTask
            {
                Metadata = metadata,
                Completion = new TaskCompletionSource<TranscriptionResult>(),
                CancellationToken = CancellationToken.None
            };

            _queue.Enqueue(task);

            _logger.LogInformation($"Added transcription task for file: {metadata.FilePath} to queue. Queue size: {_queue.Count}");

            return await task.Completion.Task;
        }

        public async Task<TranscriptionResult> QueueTranscriptionAsync(AudioFileMetadata metadata, CancellationToken cancellationToken)
        {
            var task = new TranscriptionTask
            {
                Metadata = metadata,
                Completion = new TaskCompletionSource<TranscriptionResult>(),
                CancellationToken = cancellationToken
            };

            _queue.Enqueue(task);

            _logger.LogInformation($"Added transcription task for file: {metadata.FilePath} to queue. Queue size: {_queue.Count}");

            return await task.Completion.Task;
        }

        private async Task ProcessQueueAsync()
        {
            _isProcessing = true;
            while (_isProcessing)
            {
                if (_queue.TryDequeue(out var task))
                {
                    try
                    {
                        await _semaphore.WaitAsync(task.CancellationToken);
                        
                        _logger.LogInformation($"Processing transcription for: {task.Metadata.FilePath}");
                        
                        // Use single parameter overload
                        var transcription = await _whisperService.TranscribeAsync(task.Metadata.FilePath);
                        var summary = GenerateSummary(transcription);
                        
                        task.Metadata.Transcription = transcription;
                        task.Metadata.Summary = summary;
                        task.Metadata.IsTranscribed = true;
                        task.Metadata.TranscriptionDate = DateTime.Now;
                        
                        await _db.UpdateMetadataAsync(task.Metadata);
                        
                        task.Completion.SetResult(new TranscriptionResult
                        {
                            Success = true,
                            Transcription = transcription,
                            Summary = summary,
                            Metadata = task.Metadata
                        });
                        
                        _logger.LogInformation($"Completed transcription for: {task.Metadata.FilePath}");
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning($"Transcription cancelled for: {task.Metadata.FilePath}");
                        task.Completion.SetCanceled();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error transcribing audio: {task.Metadata.FilePath}");
                        task.Completion.SetException(new Exception($"Transcription failed: {ex.Message}"));
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }
                else
                {
                    await Task.Delay(100); // Wait before checking queue again
                }
            }
        }

        public void StopProcessing()
        {
            _isProcessing = false;
            _semaphore.Dispose();
        }

        private string GenerateSummary(string transcription)
        {
            if (string.IsNullOrWhiteSpace(transcription)) return "";
            var sentences = transcription.Split('.', '!', '?');
            return sentences.Length > 0 ? sentences[0].Trim() : transcription;
        }

        public int QueueCount => _queue.Count;
        public int ActiveTranscriptions => 3 - _semaphore.CurrentCount;
    }

    public class TranscriptionTask
    {
        public AudioFileMetadata Metadata { get; set; } = null!;
        public TaskCompletionSource<TranscriptionResult> Completion { get; set; } = null!;
        public CancellationToken CancellationToken { get; set; }
    }

    public class TranscriptionResult
    {
        public bool Success { get; set; }
        public string Transcription { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public AudioFileMetadata Metadata { get; set; } = null!;
    }
}
