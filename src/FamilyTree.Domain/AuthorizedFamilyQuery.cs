namespace FamilyTree.Domain;

/// <summary>
/// The search boundary returns only the explicitly granted display field.
/// It deliberately cannot return living status, dates, contacts, photos,
/// relationships, hidden result counts, or authorization failure reasons.
/// Never construct authorization or field grants from request parameters.
/// </summary>
public sealed record AuthorizedPersonSummary(Guid PersonId, string DisplayName);

public static class AuthorizedFamilyQuery
{
    public static async Task<IReadOnlyList<AuthorizedPersonSummary>> SearchAsync(
        Guid principalId,
        IEnumerable<Person> people,
        string? text,
        IFamilyAuthorization authorization,
        FieldPrivacyPolicy fieldPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(fieldPolicy);
        if (principalId == Guid.Empty || string.IsNullOrWhiteSpace(text) ||
            text.Length > 80)
            return Array.Empty<AuthorizedPersonSummary>();

        var search = text.Trim();
        if (search.Length < 2)
            return Array.Empty<AuthorizedPersonSummary>();

        var results = new List<AuthorizedPersonSummary>();
        foreach (var person in people)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Field scope and membership scope BOTH have to permit the person.
            if (!await authorization.CanSeePersonAsync(
                    principalId, person.Id, cancellationToken) ||
                !fieldPolicy.CanRead(principalId, person.Id, ProfileField.DisplayName))
                continue;
            if (!person.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(new AuthorizedPersonSummary(person.Id, person.DisplayName));
            if (results.Count >= 25)
                break;
        }
        return results;
    }

    /// <summary>Both an unknown ID and a denied ID return null.</summary>
    public static async Task<AuthorizedPersonSummary?> GetAsync(
        Guid principalId,
        Guid personId,
        IEnumerable<Person> people,
        IFamilyAuthorization authorization,
        FieldPrivacyPolicy fieldPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(fieldPolicy);
        if (principalId == Guid.Empty || personId == Guid.Empty ||
            !await authorization.CanSeePersonAsync(
                principalId, personId, cancellationToken) ||
            !fieldPolicy.CanRead(principalId, personId, ProfileField.DisplayName))
            return null;
        var person = people.FirstOrDefault(p => p.Id == personId);
        return person is null ? null : new AuthorizedPersonSummary(person.Id, person.DisplayName);
    }
}
