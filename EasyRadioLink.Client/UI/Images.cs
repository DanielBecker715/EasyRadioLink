using System;
using System.Windows.Media.Imaging;

namespace EasyRadioLink.Client.UI;

public static class Images
{
    public static BitmapImage IconConnected;
    public static BitmapImage IconDisconnected;
    public static BitmapImage IconDisconnectedError;

    public static void Init()
    {
        // application relative pack URIs - independent of the assembly name
        // Image taken from https://icons8.com/icon/set/computer/metro @ 2018-08-01
        IconConnected = Load("status-connected.png");
        // Image taken from https://icons8.com/icon/set/computer/metro @ 2018-08-01
        IconDisconnected = Load("status-disconnected.png");
        // Image taken from https://icons8.com/icon/set/computer/metro @ 2018-08-01
        IconDisconnectedError = Load("status-disconnected-error.png");
    }

    private static BitmapImage Load(string resourceName)
    {
        return new BitmapImage(new Uri("pack://application:,,,/" + resourceName));
    }
}
