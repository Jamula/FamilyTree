namespace FamilyTree.Domain;

/// <summary>
/// Review material returned by a trusted import provider. The canonical payload
/// is an immutable, versioned serialization of everything to be reviewed and
/// applied, not an authorization credential. It contains sensitive data and
/// must not be logged or disclosed outside an authorized review.
/// </summary>
public sealed record GedcomImportReviewMaterial(
    Guid ProposalId, string CanonicalPayload, string PayloadBinding);

/// <summary>
/// Contract only for a future, explicitly enabled GEDCOM import provider
/// (issue #13; integration with #117 and #107 remains future work). No
/// implementation, registration, route, persistence, approval, or default
/// provider is supplied here. This interface is not a complete review workflow
/// or a claim of compatibility with those unmerged changes.
/// The synthetic preview's mutable collections and match suggestions are not
/// themselves an immutable reviewed payload or an authorization boundary.
///
/// Every operation must obtain the verified actor from trusted server context,
/// not from request data. Identifiers and bindings passed to this interface
/// select resources; none confer identity or authority. A provider must fail
/// closed when that context, durable storage, or current authorization is
/// unavailable. It must not be exposed until the private integration and its
/// security review are complete.
///
/// Required integration not implemented or tested here: verified-session
/// composition; encrypted durable staging and migration; branch-scoped
/// authorization and revocation synchronization; immutable canonicalization,
/// reconciliation and resolution; transactional graph writes and audit;
/// concurrency, restart, tampering, privacy and end-to-end denial tests.
/// This contract does not implement a reversal of an already applied import.
/// </summary>
public interface IReviewedGedcomImportProvider
{
    /// <summary>
    /// Atomically stage a proposal without writing the live graph. Resolve the
    /// verified submitter and check current active membership and submission
    /// authority for this exact family and branch. Snapshot the input before
    /// asynchronous use; validate bounds and supported GEDCOM semantics.
    ///
    /// From only currently authorized candidate data, produce and durably
    /// freeze a versioned canonical payload binding the original input, parsed
    /// graph, warnings, reconciliation candidates and their relevant versions,
    /// any conflict/duplicate resolutions, and the complete intended mutations.
    /// There is no resolution input or resolution workflow in this contract:
    /// if every conflict or duplicate cannot be resolved by a trusted future
    /// implementation, staging must fail closed rather than silently choose.
    /// Unauthorized items also fail closed. Bind the payload to the
    /// proposal, verified submitter, family and branch. Store it confidentially
    /// and integrity-protect it; a caller-supplied digest is not sufficient.
    /// Stage and its privacy-safe audit record commit together or not at all.
    /// </summary>
    Task<Guid> StageAsync(Guid familyId, Guid branchId, ReadOnlyMemory<byte> gedcom,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the exact persisted payload and its provider-generated binding
    /// only to a currently authorized independent reviewer of its family and
    /// branch. Recheck access on every read. No hidden candidate or graph data
    /// may be returned. Unavailable and unauthorized proposals must not become
    /// an existence oracle.
    /// </summary>
    Task<GedcomImportReviewMaterial> ReadForReviewAsync(Guid proposalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically recheck the authenticated reviewer, independence from the
    /// submitter, and both actors' current authority for the stored family and
    /// branch. Verify the binding against the exact immutable stored payload
    /// displayed for review, its integrity and pending state. Revalidate every
    /// candidate, resolution, target, graph precondition and relevant version;
    /// stale or changed reconciliation requires a new proposal and review.
    ///
    /// Apply exactly the reviewed complete plan, mark the proposal reviewed and
    /// append a privacy-safe audit record in one durable transaction. Coordinate
    /// authorization changes and revocations with that transaction so a
    /// concurrent revocation cannot be bypassed. Failure, cancellation, races,
    /// duplicate requests or replay must not cause partial or repeated writes.
    /// </summary>
    Task ApproveAsync(Guid proposalId, string payloadBinding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically verify the same reviewer, current authorization, pending
    /// state, integrity and exact reviewed binding as approval; reject without
    /// graph changes and append a privacy-safe audit record. Competing decisions,
    /// revocation, failure, cancellation and replay must fail closed.
    /// </summary>
    Task RejectAsync(Guid proposalId, string payloadBinding,
        CancellationToken cancellationToken = default);
}
