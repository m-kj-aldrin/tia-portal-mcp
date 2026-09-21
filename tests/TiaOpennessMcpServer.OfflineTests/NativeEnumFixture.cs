// Siemens-free stand-in for the public V20 bulk-attribute value struct.
// The installed contract is checked separately without any TIA attachment.
namespace Siemens.Engineering.Contract;

public readonly struct EnumToClientRepresentation
{
    public string Type { get; }
    public string Value { get; }
    public EnumToClientRepresentation(string type, string value) { Type = type; Value = value; }
    public override string ToString() => throw new InvalidOperationException("Do not stringify a wrapped enum.");
}
