using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;

namespace EasyRadioLink.Common.Helpers;

internal class JsonNetworkPropertiesResolver
{
    /// <summary>
    ///     Removes every property/field that is tagged with <see cref="JsonNetworkIgnoreSerializationAttribute" /> from
    ///     network serialisation.
    /// </summary>
    public static void StripNetworkIgnored(JsonTypeInfo jsonTypeInfo)
    {
        if (jsonTypeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        if (jsonTypeInfo.Properties is List<JsonPropertyInfo> list)
        {
            list.RemoveAll(IsNetworkIgnored);
        }
        else
        {
            var i = jsonTypeInfo.Properties.Count - 1;
            while (i > -1)
            {
                if (IsNetworkIgnored(jsonTypeInfo.Properties[i])) jsonTypeInfo.Properties.RemoveAt(i);

                i--;
            }
        }
    }

    private static bool IsNetworkIgnored(JsonPropertyInfo prop)
    {
        // the attribute sits on the member (field/property), not on the member's type
        return prop.AttributeProvider?.IsDefined(typeof(JsonNetworkIgnoreSerializationAttribute), true) == true;
    }
}
