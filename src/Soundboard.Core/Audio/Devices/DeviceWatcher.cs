using NAudio.CoreAudioApi;

namespace Soundboard.Core.Audio.Devices;

/// <summary>
/// Следит за подключением/отключением устройств и сменой устройств по умолчанию.
/// Обработчики Windows вызываются на её служебном потоке: здесь только вызывается
/// <c>onChanged</c> (он должен лишь поставить задачу в очередь), без пересборки потоков.
/// </summary>
internal sealed class DeviceWatcher : IDisposable
{
    private readonly MMDeviceNotificationClient client;
    private readonly Action<string> onChanged;
    private volatile bool disposed;

    public DeviceWatcher(MMDeviceEnumerator enumerator, Action<string> onChanged)
    {
        this.onChanged = onChanged;
        client = enumerator.CreateNotificationClient(useSynchronizationContext: false);
        client.DeviceAdded += (_, e) => Raise($"добавлено устройство {e.DeviceId}");
        client.DeviceRemoved += (_, e) => Raise($"удалено устройство {e.DeviceId}");
        client.DeviceStateChanged += (_, e) => Raise($"состояние устройства {e.DeviceId}: {e.NewState}");
        client.DefaultDeviceChanged += (_, e) => Raise($"устройство по умолчанию ({e.Flow}, {e.Role}): {e.DeviceId}");
        client.PropertyValueChanged += (_, e) =>
        {
            // Интересны только формат движка (частота/каналы) и имя устройства
            var key = e.PropertyKey;
            if (key.formatId == PropertyKeys.PKEY_AudioEngine_DeviceFormat.formatId
                || key.formatId == PropertyKeys.PKEY_Device_FriendlyName.formatId)
            {
                Raise("изменился формат или имя устройства");
            }
        };
    }

    public void Dispose()
    {
        disposed = true;
        client.Dispose();
    }

    private void Raise(string reason)
    {
        if (disposed)
            return;
        try
        {
            onChanged(reason);
        }
        catch (ObjectDisposedException)
        {
            // Движок уже остановлен
        }
    }
}
