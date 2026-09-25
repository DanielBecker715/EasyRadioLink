using System.Collections.Generic;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Common.Models;

public struct ClientListExport
{
    public ICollection<ClientInfo> Clients { get; set; }

    public string ServerVersion { get; set; }
}