using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Common.Models.EventMessages;

public class UnitUpdateMessage
{
    private ClientInfo _unitUpdate;

    public ClientInfo UnitUpdate
    {
        get => _unitUpdate;
        set
        {
            if (value == null)
            {
                _unitUpdate = null;
            }
            else
            {
                var clone = value.DeepClone();
                _unitUpdate = clone;
            }
        }
    }

    public bool FullUpdate { get; set; }
}