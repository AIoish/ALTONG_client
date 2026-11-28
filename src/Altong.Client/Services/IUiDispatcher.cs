namespace Altong.Client.Services;

public interface IUiDispatcher
{
    void Invoke(Action action);
}
