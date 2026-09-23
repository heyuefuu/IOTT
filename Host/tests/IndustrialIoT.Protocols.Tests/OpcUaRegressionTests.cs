using System.Globalization;
using System.Reflection;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.NCLink;
using Opc.Ua;
using DataType = IndustrialIoT.Domain.Enums.DataType;

internal static class OpcUaRegressionTests
{
    private static readonly Type Codec = typeof(OpcUaDriver).Assembly.GetType(
        "IndustrialIoT.Protocols.NCLink.OpcUaValueCodec", throwOnError: true)!;
    private static readonly ServiceMessageContext Context = new();

    private static TagValue Read(object? raw, DataType requested = DataType.Double, uint status = StatusCodes.Good)
    {
        var value = new DataValue { Value = raw, StatusCode = status };
        return (TagValue)Codec.GetMethod("CreateTag", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["i=2266", requested, value, Context])!;
    }

    private static DataType Map(BuiltInType type) => (DataType)Codec.GetMethod("MapBuiltInType",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [type])!;

    private static void VerifyTimeAndUnsigned()
    {
        var instant = new DateTime(2026, 9, 23, 12, 34, 56, DateTimeKind.Utc);
        var result = Read(instant);
        TestSupport.Require(result.Quality == TagQuality.Good && result.DataType == DataType.String &&
            result.Value is string timestamp && DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture) == instant,
            "DateTime with a legacy Double configuration must return an ISO timestamp");
        var unsigned = Read(ulong.MaxValue);
        TestSupport.Require(unsigned.Quality == TagQuality.Good && unsigned.DataType == DataType.String &&
            unsigned.Value is string digits && digits == "18446744073709551615", "UInt64 precision was lost");
        var signed = Read(long.MinValue, DataType.Int64);
        TestSupport.Require(signed.Quality == TagQuality.Good && signed.DataType == DataType.String &&
            signed.Value is string signedDigits && signedDigits == "-9223372036854775808", "Int64 precision was lost");
        TestSupport.Require(Map(BuiltInType.DateTime) == DataType.String && Map(BuiltInType.UInt64) == DataType.String &&
            Map(BuiltInType.ExtensionObject) == DataType.String, "Non-numeric UA types mapped to a numeric domain type");
    }

    private static void RequireJson(object raw, params string[] expected)
    {
        var result = Read(raw);
        TestSupport.Require(result.Quality == TagQuality.Good && result.DataType == DataType.String && result.Value is string,
            $"{raw.GetType().Name} did not produce a displayable JSON value");
        var json = (string)result.Value;
        using var document = JsonDocument.Parse(json);
        foreach (var content in expected)
            TestSupport.Require(json.Contains(content, StringComparison.Ordinal), $"UA JSON omitted content {content}: {json}");
    }

    private static void VerifyCollections()
    {
        RequireJson(new[] { 123, 456, 789 }, "123", "456", "789");
        RequireJson(new[] { new Variant(123), new Variant("variant-text") }, "123", "variant-text");
        RequireJson(new[] { new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc) }, "2026-09-23");
        RequireJson(new Matrix(new[] { 123, 456, 789, 987 }, BuiltInType.Int32, [2, 2]), "123", "456", "789", "987");
        RequireJson(new ExtensionObject(new BuildInfo { ProductName = "regression-product", BuildNumber = "build-123" }),
            "regression-product", "build-123");
    }

    private static void VerifyQuality()
    {
        TestSupport.Require(Read(null).Value is null, "A null OPC UA value must not become numeric zero");
        var bad = Read("unconvertible", DataType.Double, StatusCodes.BadNodeIdUnknown);
        TestSupport.Require(bad.Quality == TagQuality.Bad && bad.Value is null && !string.IsNullOrWhiteSpace(bad.ErrorMessage),
            "Bad OPC UA status must preserve a diagnostic and null value without throwing");
        var uncertain = Read(12d, DataType.Double, StatusCodes.Uncertain);
        TestSupport.Require(uncertain.Quality == TagQuality.Uncertain && Equals(uncertain.Value, 12d),
            "Uncertain OPC UA status must not be promoted to Good or lose its value");
        var invalid = Read("not-a-number");
        TestSupport.Require(invalid.Quality == TagQuality.Bad && invalid.Value is null && !string.IsNullOrWhiteSpace(invalid.ErrorMessage),
            "An invalid scalar conversion must fail only that point without throwing");
        TestSupport.Require(Read(45d).Quality == TagQuality.Good, "A failed conversion affected a later valid point");
    }

    private static void VerifyAdditionalBuiltIns()
    {
        TestSupport.Require(Map(BuiltInType.Byte) == DataType.UInt16 && Map(BuiltInType.SByte) == DataType.Int16 &&
            Map(BuiltInType.Enumeration) == DataType.Int32 && Map(BuiltInType.ByteString) == DataType.ByteArray,
            "Byte, SByte, Enumeration or ByteString mapping is incorrect");
        TestSupport.Require(Equals(Read((byte)255, DataType.UInt16).Value, (ushort)255) &&
            Equals(Read((sbyte)-128, DataType.Int16).Value, (short)-128) &&
            Equals(Read(ServerState.Running, DataType.Int32).Value, (int)ServerState.Running),
            "Byte, SByte or enumeration conversion lost its value");
        var identifier = Guid.Parse("a3bdf958-051b-41fc-8a66-8ae823ba36ab");
        var xml = new System.Xml.XmlDocument();
        xml.LoadXml("<machine><state>ready</state></machine>");
        foreach (var sample in new (object Value, string Content)[]
        {
            (identifier, identifier.ToString()), (new Uuid(identifier), identifier.ToString()),
            (new NodeId("regression-node", 2), "regression-node"),
            (new ExpandedNodeId("regression-expanded", 2), "regression-expanded"),
            (new QualifiedName("regression-name", 2), "regression-name"),
            (new LocalizedText("en", "regression-text"), "regression-text"),
            (new StatusCode(StatusCodes.BadNodeIdUnknown), "BadNodeIdUnknown"),
            (xml.DocumentElement!, "<machine><state>ready</state></machine>")
        })
        {
            var result = Read(sample.Value);
            TestSupport.Require(result.Quality == TagQuality.Good && result.DataType == DataType.String &&
                result.Value is string text && text.Contains(sample.Content, StringComparison.Ordinal),
                $"UA {sample.Value.GetType().Name} did not preserve readable content");
        }
        var binary = Read(new byte[] { 0, 127, 255 });
        TestSupport.Require(binary.Quality == TagQuality.Good && binary.DataType == DataType.ByteArray &&
            binary.Value is byte[] bytes && bytes.SequenceEqual(new byte[] { 0, 127, 255 }), "ByteString content changed");
    }

    private static void VerifyNonFinite()
    {
        foreach (var special in new object[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var result = Read(special, special is float ? DataType.Float : DataType.Double);
            TestSupport.Require(result.Quality == TagQuality.Good && result.DataType == DataType.String &&
                result.Value is string display && display == Convert.ToString(special, CultureInfo.InvariantCulture),
                "Non-finite numeric value must become a display string");
            using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
            TestSupport.Require(serialized.RootElement.GetProperty("Value").ValueKind == JsonValueKind.String,
                "Non-finite numeric value is not safe for API JSON serialization");
        }
    }

    public static Task RunAsync()
    {
        foreach (var sample in new (object Value, DataType Type)[]
        {
            (true, DataType.Bool), ((short)-123, DataType.Int16), (-456, DataType.Int32),
            (123456789L, DataType.Int64), ((ushort)65535, DataType.UInt16), (uint.MaxValue, DataType.UInt32),
            (1.25f, DataType.Float), (3.5d, DataType.Double), ("machine-ready", DataType.String)
        })
        {
            var result = Read(sample.Value, sample.Type);
            TestSupport.Require(result.Quality == TagQuality.Good && Equals(result.Value, sample.Value),
                $"OPC UA scalar {sample.Type} lost its value or quality");
        }
        VerifyTimeAndUnsigned();
        VerifyCollections();
        VerifyQuality();
        VerifyAdditionalBuiltIns();
        VerifyNonFinite();
        return Task.CompletedTask;
    }
}
