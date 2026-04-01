namespace Olve.Pipelines.Shared;

public class Event<T>
{
    private Action<T>? _handlers;

    public void Invoke(T message) => _handlers?.Invoke(message);
    public void Subscribe(Action<T> handler) => _handlers += handler;
    public void Unsubscribe(Action<T> handler) => _handlers -= handler;
}
