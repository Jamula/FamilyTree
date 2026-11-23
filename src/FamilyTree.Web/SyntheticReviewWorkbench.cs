using FamilyTree.Domain;

namespace FamilyTree.Web;

/// <summary>
/// Purely fictional, process-local genealogy review playground.
/// Uses fixed synthetic authors/reviewers and GENERATED fictional names;
/// it cannot read user data, use identities, or persist a real family record.
/// No operation is eligible for production private-family routing.
/// </summary>
public sealed class SyntheticReviewWorkbench
{
    private readonly object _gate = new();
    private readonly Guid _family = Guid.NewGuid();
    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _moderator = Guid.NewGuid();
    private readonly ReviewedGenealogyStore _store;
    private readonly Person _founder;
    private ReviewedGenealogyStore.PendingEdit? _pending;
    private readonly Stack<long> _reversible = new();
    private int _guestSequence;

    public sealed record View(
        IReadOnlyList<Person> People,
        IReadOnlyList<Relationship> Relationships,
        IReadOnlyList<ReviewedGenealogyStore.AuditEntry> Audit,
        string? PendingKind,
        int RemainingGuests,
        bool HasUndo);

    public SyntheticReviewWorkbench()
    {
        _store = new ReviewedGenealogyStore(_family);
        _founder = new Person(Guid.NewGuid(), "Fictional Founder");
        Seed(_founder);
        Seed(new Person(Guid.NewGuid(), "Fictional Relative"));
    }

    public View Read()
    {
        lock (_gate)
            return new(_store.People, _store.Relationships, _store.Audit,
                _pending?.Kind.ToString(), 6 - _guestSequence, _reversible.Count > 0);
    }

    public bool Execute(string? action)
    {
        lock (_gate)
        {
            switch (action)
            {
                case "propose-person":
                    if (_pending is not null || _guestSequence >= 6) return false;
                    var guest = new Person(Guid.NewGuid(), $"Fictional Guest {++_guestSequence}");
                    _pending = _store.ProposePerson(_family, _author, guest);
                    return true;

                case "propose-link":
                    // Cap even repeated propose/approve/undo cycles; this playground
                    // is intentionally bounded and process-local.
                    if (_pending is not null || _store.Relationships.Count >= 10 ||
                        _store.Audit.Count >= 80) return false;
                    var targets = _store.People.Where(p => p.Id != _founder.Id).ToArray();
                    if (targets.Length == 0) return false;
                    var target = targets[^1];
                    var link = Relationship.Create(_founder.Id, target.Id,
                        RelationKind.Friend);
                    _pending = _store.ProposeRelationship(_family, _author, link);
                    return true;

                case "approve":
                    if (_pending is null) return false;
                    var reviewed = _store.Review(_pending.Id, _moderator, _family,
                        authorizedModerator: true, approve: true);
                    _reversible.Push(reviewed.Revision);
                    _pending = null;
                    return true;

                case "reject":
                    if (_pending is null) return false;
                    _store.Review(_pending.Id, _moderator, _family,
                        authorizedModerator: true, approve: false);
                    _pending = null;
                    return true;

                case "undo":
                    if (_pending is not null || _reversible.Count == 0) return false;
                    // Undo follows reverse approval order, preserving references.
                    _store.Undo(_reversible.Peek(), _moderator, _family,
                        authorizedModerator: true);
                    _reversible.Pop();
                    return true;

                default:
                    return false;
            }
        }
    }

    private void Seed(Person person)
    {
        var proposal = _store.ProposePerson(_family, _author, person);
        _store.Review(proposal.Id, _moderator, _family,
            authorizedModerator: true, approve: true);
        // Seeds are not exposed as undo candidates in the playground.
    }
}
