using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyRadioLink.Client.Settings.Favourites;

public class ServerAddress : INotifyPropertyChanged
{
    private string _address;

    private string _password;

    private bool _isDefault;

    private string _name;

    /// <param name="password">server password, null or "" for an open server</param>
    public ServerAddress(string name, string address, string password, bool isDefault)
    {
        // Set private values directly so we don't trigger useless re-saving of favourites list when being loaded for the first time
        _name = name;
        _address = address;
        _password = password;
        IsDefault = isDefault; // Explicitly use property setter here since IsDefault change includes additional logic
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public string Address
    {
        get => _address;
        set
        {
            if (_address != value)
            {
                _address = value;
                OnPropertyChanged();
            }
        }
    }

    // server password (optional)
    public string Password
    {
        get => _password;
        set
        {
            if (_password != value)
            {
                _password = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            _isDefault = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}