using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyRadioLink.Common.Helpers;

public class PropertyChangedBaseClass : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        try
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        catch (Exception)
        {
        }
    }
}