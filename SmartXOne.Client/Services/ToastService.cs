namespace SmartXOne.Client.Services;

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Danger,
}

public sealed record ToastMessage(int Id, string Text, ToastLevel Level);

/// <summary>
/// App-wide "crisp feedback" channel: any component calls <see cref="Show"/>
/// and a single <c>ToastHost</c> mounted once in <c>MainLayout</c> renders it,
/// so every success/error toast in the app - a sensor registering, a file
/// uploading, an issue being reported, a sensor crossing into an anomaly
/// alert band - appears in the same place with the same look, instead of
/// each component building its own toast UI.
/// </summary>
public sealed class ToastService(ILogger<ToastService> logger)
{
    private int _nextId;

    public event Action<ToastMessage>? OnShow;

    /// <summary>
    /// Raises a toast. Never throws: it's usually called from inside another
    /// component's catch block, where a second failure would hide the first.
    /// </summary>
    public void Show(string text, ToastLevel level = ToastLevel.Info)
    {
        try
        {
            OnShow?.Invoke(new ToastMessage(++_nextId, text, level));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Toast host failed to show '{Text}' ({Level}).", text, level);
        }
    }
}
