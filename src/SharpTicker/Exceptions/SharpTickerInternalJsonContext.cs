using System.Text.Json.Serialization;

namespace SharpTicker.Exceptions
{
    [JsonSerializable(typeof(ExceptionDetailClassForSerialization))]
    internal partial class SharpTickerInternalJsonContext : JsonSerializerContext { }
}
