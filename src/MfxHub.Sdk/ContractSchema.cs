namespace MfxHub;

/// <summary>
/// The contract object a public type represents: the key of <c>components.schemas</c> in the API description
/// (<c>spec/openapi.yaml</c>), exactly as written there. The SDK reference (<c>docs/sdkref.json</c>) names it in
/// the type's <c>schema</c>, which the docs site links to the object's fields.
/// </summary>
/// <remarks>
/// <c>tools/generate.ps1</c> writes it on every generated model whose class NSwag named after a schema key, so
/// the name is the contract's even where the class name is not. A hand-written type carries it only when it
/// represents exactly one schema, such as a stream event read from that event's <c>data</c>.
/// </remarks>
/// <param name="name">The schema's key.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum, Inherited = false)]
internal sealed class ContractSchemaAttribute(string name) : Attribute
{
    /// <summary>The schema's key, case kept.</summary>
    public string Name { get; } = name;
}
