using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Fluxor;
using SimpleIPaaS.Client.Store;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Services;

// Owns the async AI build job for the whole WASM app so the user can leave the
// modal (and the designer) while the agent works. Polling never throws: a failed
// tick is skipped and the next one retries, per the SafeFetch convention.
public sealed class AiBuildJobTracker : IDisposable
{
    private const string Queued = "Queued";
    private const string Running = "Running";
    private const string Completed = "Completed";
    private const string Failed = "Failed";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxRestoreAttempts = 5;

    private readonly HttpClient _http;
    private readonly IDispatcher _dispatcher;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _restoreCts;
    private Task? _restoreTask;

    public AiBuildJobTracker(HttpClient http, IDispatcher dispatcher)
    {
        _http = http;
        _dispatcher = dispatcher;
    }

    public AiBuildJobDto? CurrentJob { get; private set; }
    public bool IsBusy => CurrentJob is { Status: Queued or Running };
    public bool IsRestoring { get; private set; }
    public event Action? Changed;

    public Task RestoreActiveJobAsync()
    {
        if (CurrentJob != null) return Task.CompletedTask;
        if (_restoreTask != null) return _restoreTask;
        _restoreTask = RestoreActiveJobCoreAsync();
        return _restoreTask;
    }

    private async Task RestoreActiveJobCoreAsync()
    {
        IsRestoring = true;
        _restoreCts = new CancellationTokenSource();
        Changed?.Invoke();
        try
        {
            for (var attempt = 0; attempt < MaxRestoreAttempts; attempt++)
            {
                AiBuildJobDto[]? jobs;
                try
                {
                    jobs = await _http.GetFromJsonAsync<AiBuildJobDto[]>("api/ai/flow/jobs", _restoreCts.Token);
                }
                catch (OperationCanceledException) when (_restoreCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Only a failed call is worth retrying; a successful empty response means there
                    // is no job to follow and must not leave the top bar on "Checking AI builds…".
                    if (attempt + 1 < MaxRestoreAttempts)
                        await Task.Delay(RestoreRetryDelay, _restoreCts.Token);
                    continue;
                }

                var activeJob = jobs?
                    .Where(job => job.Status is Queued or Running)
                    .OrderByDescending(job => job.CreatedAt)
                    .FirstOrDefault();
                if (activeJob != null)
                {
                    Follow(activeJob);
                }

                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            _restoreCts?.Dispose();
            _restoreCts = null;
            _restoreTask = null;
            IsRestoring = false;
            Changed?.Invoke();
        }
    }

    public async Task<AiBuildJobDto?> CreateAsync(Guid? providerId, string instruction, IntegrationFlowDto? currentFlow)
    {
        var job = await SafeFetch.SendAsync<AiBuildJobDto>(
            () => _http.PostAsJsonAsync("api/ai/flow/jobs", new AiBuildJobCreateDto
            {
                ProviderId = providerId,
                Instruction = instruction,
                CurrentFlow = currentFlow
            }),
            _dispatcher,
            "the AI build request");
        if (job == null) return null;

        _restoreTask = null;
        CurrentJob = job;
        Changed?.Invoke();
        StartPolling(job.Id);
        return job;
    }

    public void Follow(AiBuildJobDto job)
    {
        _restoreCts?.Cancel();
        CurrentJob = job;
        Changed?.Invoke();
        if (job.Status is Queued or Running) StartPolling(job.Id);
    }

    public void Clear()
    {
        _cts?.Cancel();
        CurrentJob = null;
        Changed?.Invoke();
    }

    private void StartPolling(Guid jobId)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = PollAsync(jobId, _cts.Token);
    }

    private async Task PollAsync(Guid jobId, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, token);
                AiBuildJobDto? job;
                try
                {
                    job = await _http.GetFromJsonAsync<AiBuildJobDto>($"api/ai/flow/jobs/{jobId}", token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    continue;
                }
                if (job == null) continue;

                CurrentJob = job;
                Changed?.Invoke();

                if (job.Status == Completed)
                {
                    _dispatcher.Dispatch(new ShowToastAction { Message = "AI draft is ready to review.", Level = "success" });
                    return;
                }
                if (job.Status == Failed)
                {
                    _dispatcher.Dispatch(new ShowToastAction
                    {
                        Message = string.IsNullOrWhiteSpace(job.Error) ? "The AI build failed." : "AI build failed: " + job.Error,
                        Level = "error"
                    });
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _restoreCts?.Cancel();
    }
}
