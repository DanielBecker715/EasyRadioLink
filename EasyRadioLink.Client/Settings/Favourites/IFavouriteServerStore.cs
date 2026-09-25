using System.Collections.Generic;

namespace EasyRadioLink.Client.Settings.Favourites;

public interface IFavouriteServerStore
{
    IEnumerable<ServerAddress> LoadFromStore();

    bool SaveToStore(IEnumerable<ServerAddress> addresses);
}