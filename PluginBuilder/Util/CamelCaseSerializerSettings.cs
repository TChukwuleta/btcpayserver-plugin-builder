using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace PluginBuilder.Util;

public class CamelCaseSerializerSettings
{
    public static readonly JsonSerializerSettings Instance = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(), DefaultValueHandling = DefaultValueHandling.Ignore
    };
}
