using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AiAudioRecoder.Models;

namespace AiAudioRecoder.Services
{
    public enum TranscriptionTaskStatus { Queued, Running, Completed, Cancelled, Failed }

    public class TranscriptionTask
    {
        public Guid Id { get; } = Guid.NewGuid();
        public AudioFileMetadata Metadata { get; set; } = null!;
        public int Priority { get; set; }
        public TaskCompletionSource<TranscriptionResult> Completion { get; set; } = null!;
        public CancellationTokenSource Cancellation { get; set; } = new();
        public TranscriptionTaskStatus Status { get; set; } = TranscriptionTaskStatus.Queued;
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
    }

    public class TranscriptionResult
    {
        public bool Success { get; set; }
        public string Transcription { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public AudioFileMetadata Metadata { get; set; } = null!;
    }

    public class TranscriptionQueueService
    {
        private readonly WhisperTranscriptionService _whisperService;
        private readonly AudioMetadataDatabase _db;
        private readonly ILogger<TranscriptionQueueService> _logger;
        private readonly int _maxConcurrent;

        private readonly List<TranscriptionTask> _tasks = new(); // queued + running + completed
        private readonly object _lock = new();
        private bool _running = true;

        public TranscriptionQueueService(ILogger<TranscriptionQueueService> logger,
            WhisperTranscriptionService whisperService,
            AudioMetadataDatabase db)
        {
            _logger = logger;
            _whisperService = whisperService;
            _db = db;
            _maxConcurrent = 3;
            Task.Run(DispatchLoopAsync);
        }

        #region Public API
        public Task<TranscriptionResult> QueueTranscriptionAsync(AudioFileMetadata metadata, int priority = 0)
        {
            // Avoid double queue if already queued or running
            lock (_lock)
            {
                if (_tasks.Exists(t => t.Metadata.FilePath == metadata.FilePath && (t.Status == TranscriptionTaskStatus.Queued || t.Status == TranscriptionTaskStatus.Running)))
                {
                    var existing = _tasks.Find(t => t.Metadata.FilePath == metadata.FilePath && (t.Status == TranscriptionTaskStatus.Queued || t.Status == TranscriptionTaskStatus.Running))!;
                    return existing.Completion.Task; // Return existing task
                }
            }

            var task = new TranscriptionTask
            {
                Metadata = metadata,
                Priority = priority,
                Completion = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            lock (_lock)
            {
                _tasks.Add(task);
                // Keep list roughly ordered (descending priority, ascending enqueue time)
                _tasks.Sort((a, b) =>
                {
                    int p = b.Priority.CompareTo(a.Priority);
                    return p != 0 ? p : a.EnqueuedAt.CompareTo(b.EnqueuedAt);
                });
            }
            _logger.LogInformation("Queued transcription {File} with priority {Priority}", metadata.FilePath, priority);
            return task.Completion.Task;
        }

        public bool Cancel(Guid id)
        {
            lock (_lock)
            {
                var task = _tasks.Find(t => t.Id == id);
                if (task == null) return false;
                if (task.Status == TranscriptionTaskStatus.Queued)
                {
                    task.Status = TranscriptionTaskStatus.Cancelled;
                    task.Completion.TrySetCanceled();
                    return true;
                }
                if (task.Status == TranscriptionTaskStatus.Running)
                {
                    task.Cancellation.Cancel();
                    return true;
                }
            }
            return false;
        }

        public bool ChangePriority(Guid id, int newPriority)
        {
            lock (_lock)
            {
                var task = _tasks.Find(t => t.Id == id);
                if (task == null || task.Status != TranscriptionTaskStatus.Queued) return false;
                task.Priority = newPriority;
                _tasks.Sort((a, b) =>
                {
                    int p = b.Priority.CompareTo(a.Priority);
                    return p != 0 ? p : a.EnqueuedAt.CompareTo(b.EnqueuedAt);
                });
                return true;
            }
        }

        public IEnumerable<TranscriptionTask> GetTasksSnapshot()
        {
            lock (_lock)
            {
                return _tasks.ToArray();
            }
        }

        public int QueueCount
        {
            get
            {
                lock (_lock) return _tasks.FindAll(t => t.Status == TranscriptionTaskStatus.Queued).Count;
            }
        }

        public int ActiveTranscriptions
        {
            get
            {
                lock (_lock) return _tasks.FindAll(t => t.Status == TranscriptionTaskStatus.Running).Count;
            }
        }

        public void StopProcessing()
        {
            _running = false;
        }
        #endregion

        private async Task DispatchLoopAsync()
        {
            while (_running)
            {
                List<TranscriptionTask> toStart = new();
                lock (_lock)
                {
                    int runningCount = _tasks.FindAll(t => t.Status == TranscriptionTaskStatus.Running).Count;
                    if (runningCount < _maxConcurrent)
                    {
                        int availableSlots = _maxConcurrent - runningCount;
                        foreach (var task in _tasks)
                        {
                            if (availableSlots <= 0) break;
                            if (task.Status == TranscriptionTaskStatus.Queued)
                            {
                                task.Status = TranscriptionTaskStatus.Running;
                                toStart.Add(task);
                                availableSlots--;
                            }
                        }
                    }
                }

                foreach (var task in toStart)
                {
                    _ = ProcessTaskAsync(task); // fire & forget
                }

                await Task.Delay(150);
            }
        }

        private async Task ProcessTaskAsync(TranscriptionTask task)
        {
            try
            {
                _logger.LogInformation("Starting transcription {File}", task.Metadata.FilePath);
                var transcript = await _whisperService.TranscribeAsync(task.Metadata.FilePath, task.Cancellation.Token);
                if (task.Cancellation.IsCancellationRequested)
                {
                    task.Status = TranscriptionTaskStatus.Cancelled;
                    task.Completion.TrySetCanceled();
                    return;
                }
                var summary = GenerateSummary(transcript);
                task.Metadata.Transcription = transcript;
                task.Metadata.Summary = summary;
                task.Metadata.IsTranscribed = true;
                task.Metadata.TranscriptionDate = DateTime.Now;
                await _db.UpdateMetadataAsync(task.Metadata);
                task.Status = TranscriptionTaskStatus.Completed;
                task.Completion.TrySetResult(new TranscriptionResult
                {
                    Success = true,
                    Transcription = transcript,
                    Summary = summary,
                    Metadata = task.Metadata
                });
                _logger.LogInformation("Finished transcription {File}", task.Metadata.FilePath);
            }
            catch (OperationCanceledException)
            {
                task.Status = TranscriptionTaskStatus.Cancelled;
                task.Completion.TrySetCanceled();
                _logger.LogWarning("Transcription cancelled {File}", task.Metadata.FilePath);
            }
            catch (Exception ex)
            {
                task.Status = TranscriptionTaskStatus.Failed;
                task.Completion.TrySetResult(new TranscriptionResult
                {
                    Success = false,
                    Transcription = string.Empty,
                    Summary = string.Empty,
                    Metadata = task.Metadata
                });
                task.Metadata.IsTranscribed = false;
                await _db.UpdateMetadataAsync(task.Metadata);
                _logger.LogError(ex, "Transcription failed {File}" , task.Metadata.FilePath);
            }
        }

        private string GenerateSummary(string transcription)
        {
            if (string.IsNullOrWhiteSpace(transcription)) return string.Empty;
            var sentences = transcription.Split('.', '!', '?');
            return sentences.Length > 0 ? sentences[0].Trim() : transcription;
        }
    }
}
