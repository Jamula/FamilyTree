namespace FamilyTree.Domain;

/// <summary>
/// Nonpersistent prototype of a moderated family graph. No HTTP route exposes
/// this store. Caller identities/roles MUST come from a trusted authenticated
/// server, never JSON flags. Audit entries intentionally exclude private names.
/// </summary>
public sealed class ReviewedGenealogyStore
{
    public enum EditKind { AddPerson, AddRelationship }
    public sealed record PendingEdit(Guid Id, Guid FamilyId, Guid SubmitterId,
        EditKind Kind, Guid SubjectId);
    public sealed record AuditEntry(long Revision, Guid ProposalId, Guid ReviewerId,
        EditKind Kind, bool Approved, bool Reverted);

    private readonly object _gate = new();
    private readonly Guid _familyId;
    private readonly Dictionary<Guid, Person> _people = new();
    private readonly Dictionary<Guid, Relationship> _relationships = new();
    private readonly Dictionary<Guid, (PendingEdit Request, Person? Person, Relationship? Link)> _pending = new();
    private readonly List<AuditEntry> _audit = new();
    private long _revision;

    public ReviewedGenealogyStore(Guid familyId)
    {
        if (familyId == Guid.Empty) throw new ArgumentException("Family ID required.", nameof(familyId));
        _familyId = familyId;
    }

    public IReadOnlyList<Person> People
    {
        get { lock (_gate) return _people.Values.ToArray(); }
    }
    public IReadOnlyList<Relationship> Relationships
    {
        get { lock (_gate) return _relationships.Values.ToArray(); }
    }
    public IReadOnlyList<AuditEntry> Audit
    {
        get { lock (_gate) return _audit.ToArray(); }
    }

    public PendingEdit ProposePerson(Guid familyId, Guid submitterId, Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        RequireSubmission(familyId, submitterId);
        if (person.Id == Guid.Empty || string.IsNullOrWhiteSpace(person.DisplayName) ||
            person.DisplayName.Length > 200)
            throw new ArgumentException("Invalid person.");
        lock (_gate)
        {
            if (_people.ContainsKey(person.Id) || _pending.Values.Any(x => x.Person?.Id == person.Id))
                throw new InvalidOperationException("Duplicate person.");
            var request = new PendingEdit(Guid.NewGuid(), _familyId, submitterId,
                EditKind.AddPerson, person.Id);
            _pending.Add(request.Id, (request, person, null));
            return request;
        }
    }

    public PendingEdit ProposeRelationship(Guid familyId, Guid submitterId, Relationship relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        RequireSubmission(familyId, submitterId);
        if (relation.Id == Guid.Empty || relation.FromPersonId == relation.ToPersonId ||
            relation.FromPersonId == Guid.Empty || relation.ToPersonId == Guid.Empty ||
            !Enum.IsDefined(relation.Kind) ||
            (relation.Kind == RelationKind.Other && string.IsNullOrWhiteSpace(relation.Label)))
            throw new ArgumentException("Invalid relationship.");
        lock (_gate)
        {
            if (!_people.ContainsKey(relation.FromPersonId) ||
                !_people.ContainsKey(relation.ToPersonId))
                throw new InvalidOperationException("Endpoints must already be approved.");
            if (_relationships.ContainsKey(relation.Id) ||
                _pending.Values.Any(x => x.Link?.Id == relation.Id))
                throw new InvalidOperationException("Duplicate relationship ID.");
            var request = new PendingEdit(Guid.NewGuid(), _familyId, submitterId,
                EditKind.AddRelationship, relation.FromPersonId);
            _pending.Add(request.Id, (request, null, relation));
            return request;
        }
    }

    public AuditEntry Review(Guid proposalId, Guid moderatorId, Guid moderatorFamilyId,
        bool authorizedModerator, bool approve)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(proposalId, out var entry))
                throw new InvalidOperationException("Proposal unavailable.");
            if (moderatorId == Guid.Empty || moderatorId == entry.Request.SubmitterId ||
                moderatorFamilyId != _familyId || !authorizedModerator)
                throw new UnauthorizedAccessException("Independent reviewer required.");
            if (approve && entry.Person is not null && _people.ContainsKey(entry.Person.Id))
                throw new InvalidOperationException("Person already exists.");
            if (approve && entry.Link is not null &&
                (!_people.ContainsKey(entry.Link.FromPersonId) ||
                !_people.ContainsKey(entry.Link.ToPersonId) ||
                _relationships.ContainsKey(entry.Link.Id)))
                throw new InvalidOperationException("Relationship has invalid endpoints or ID.");
            if (approve)
            {
                if (entry.Person is not null) _people.Add(entry.Person.Id, entry.Person);
                if (entry.Link is not null) _relationships.Add(entry.Link.Id, entry.Link);
            }
            _pending.Remove(proposalId);
            var audit = new AuditEntry(++_revision, proposalId, moderatorId,
                entry.Request.Kind, approve, false);
            _audit.Add(audit);
            return audit;
        }
    }

    private void RequireSubmission(Guid familyId, Guid submitterId)
    {
        if (familyId != _familyId || submitterId == Guid.Empty)
            throw new UnauthorizedAccessException("Wrong family or unauthenticated author.");
    }
}
