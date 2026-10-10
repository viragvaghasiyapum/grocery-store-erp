using System.Text.Json;
using System.Text.Json.Serialization;
using GroceryErp.Core.Numerics;

namespace GroceryErp.Sales;

public static class PrintJobJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyConverter());
        options.Converters.Add(new QuantityConverter());
        options.Converters.Add(new PercentConverter());
        return options;
    }

    private sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetDecimal());
        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.Value);
    }

    private sealed class QuantityConverter : JsonConverter<Quantity>
    {
        public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetDecimal());
        public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.Value);
    }

    private sealed class PercentConverter : JsonConverter<Percent>
    {
        public override Percent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetDecimal());
        public override void Write(Utf8JsonWriter writer, Percent value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.Value);
    }
}
