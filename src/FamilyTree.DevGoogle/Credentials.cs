namespace FamilyTree.DevGoogle;

// Avoid record-generated ToString() exposing either value.
internal sealed class Credentials(string clientId, string clientSecret)
{
    public string ClientId { get; } = Validate(clientId);
    public string ClientSecret { get; } = Validate(clientSecret);

    private static string Validate(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 1000 &&
        !value.Any(char.IsControl) ? value : throw new InvalidOperationException();
}
