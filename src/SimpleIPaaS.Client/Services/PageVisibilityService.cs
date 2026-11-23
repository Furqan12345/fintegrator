using Microsoft.JSInterop;

namespace SimpleIPaaS.Client.Services;

// Tells background pollers whether the tab is on screen. A hidden tab has nobody looking at it, so the
// health check, live log tail and run-list refresh pause instead of costing the browser, the API and
// the database a request every few seconds. Pollers that read a cursor (the log tail) simply catch up
// on the next tick after the tab becomes visible again.
public sealed class PageVisibilityService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<PageVisibilityService>? _reference;
    private bool _started;

    public PageVisibilityService(IJSRuntime js) => _js = js;

    public bool IsVisible { get; private set; } = true;

    public event Action<bool>? Changed;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            _reference = DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("simpleIPaaSVisibility.start", _reference);
        }
        catch (JSException)
        {
            // Without the helper the tab is simply treated as always visible.
        }
    }

    [JSInvokable]
    public void SetVisible(bool visible)
    {
        if (IsVisible == visible) return;
        IsVisible = visible;
        Changed?.Invoke(visible);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _js.InvokeVoidAsync("simpleIPaaSVisibility.stop"); }
        catch (Exception) { /* the page is going away */ }
        _reference?.Dispose();
        _reference = null;
    }
}
