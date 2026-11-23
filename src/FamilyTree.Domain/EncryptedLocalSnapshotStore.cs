using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FamilyTree.Domain;

/// <summary>
/// Dev-only at-rest authenticated encryption for one local family snapshot.
/// The 256-bit key must be provided by a trusted secret store, never checked
/// into a public repo. No API endpoint or production-data path uses this.
/// This is not a transactional database, backup solution or OS ACL policy.
/// </summary>
public sealed record LocalFamilySnapshot(Guid FamilyId, long Revision,
    IReadOnlyList<Person> People, IReadOnlyList<Relationship> Relationships);

public sealed class EncryptedLocalSnapshotStore : IDisposable
{
    private static readonly byte[] Magic = "FT01"u8.ToArray();
    private static readonly byte[] Context = "familytree.local.snapshot.v1"u8.ToArray();
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxPayload = 2_000_000;
    private readonly string _path;
    private readonly byte[] _key;
    private bool _disposed;

    public EncryptedLocalSnapshotStore(string absolutePath, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) ||
            !Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("An absolute snapshot file path is required.",nameof(absolutePath));
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("A 256-bit encryption key is required.",nameof(key));
        _path = Path.GetFullPath(absolutePath);
        _key = (byte[])key.Clone();
    }

    public async Task SaveAsync(LocalFamilySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Validate(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        try
        {
            if (plaintext.Length > MaxPayload)
                throw new InvalidOperationException("Snapshot exceeds the local maximum.");
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagBytes];
            using (var aes = new AesGcm(_key, TagBytes))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Context);
            var envelope = new byte[Magic.Length + NonceBytes + TagBytes + ciphertext.Length];
            Magic.CopyTo(envelope, 0);
            nonce.CopyTo(envelope, Magic.Length);
            tag.CopyTo(envelope, Magic.Length + NonceBytes);
            ciphertext.CopyTo(envelope, Magic.Length + NonceBytes + TagBytes);
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, envelope, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<LocalFamilySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = new FileInfo(_path);
        if (!file.Exists) throw new FileNotFoundException("Local snapshot is absent.");
        if (file.Length < Magic.Length + NonceBytes + TagBytes + 1 ||
            file.Length > MaxPayload + Magic.Length + NonceBytes + TagBytes)
            throw new InvalidDataException("Snapshot envelope invalid.");
        var content = await File.ReadAllBytesAsync(_path, cancellationToken);
        if (!content.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Snapshot envelope invalid.");
        var nonce = content.AsSpan(Magic.Length, NonceBytes);
        var tag = content.AsSpan(Magic.Length + NonceBytes, TagBytes);
        var cipher = content.AsSpan(Magic.Length + NonceBytes + TagBytes);
        var clear = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, clear, Context);
            var result = JsonSerializer.Deserialize<LocalFamilySnapshot>(clear)
                ?? throw new InvalidDataException("Snapshot payload invalid.");
            Validate(result);
            return result;
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Snapshot could not be authenticated.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Snapshot payload invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }

    private static void Validate(LocalFamilySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.FamilyId == Guid.Empty || snapshot.Revision < 0 ||
            snapshot.People is null || snapshot.Relationships is null ||
            snapshot.People.Count > 10000 || snapshot.Relationships.Count > 20000)
            throw new InvalidDataException("Snapshot structure invalid.");
        var ids = new HashSet<Guid>();
        foreach (var person in snapshot.People)
            if (person is null || person.Id == Guid.Empty || !ids.Add(person.Id) ||
                string.IsNullOrWhiteSpace(person.DisplayName) ||
                person.DisplayName.Length > 200)
                throw new InvalidDataException("Snapshot structure invalid.");
        var links = new HashSet<Guid>();
        foreach (var edge in snapshot.Relationships)
            if (edge is null || edge.Id == Guid.Empty || !links.Add(edge.Id) ||
                !ids.Contains(edge.FromPersonId) || !ids.Contains(edge.ToPersonId) ||
                edge.FromPersonId == edge.ToPersonId || !Enum.IsDefined(edge.Kind) ||
                (edge.Kind == RelationKind.Other && string.IsNullOrWhiteSpace(edge.Label)) ||
                (edge.Label is not null && (edge.Label.Length > 200 ||
                    edge.Label.IndexOfAny(['\r', '\n']) >= 0)))
                throw new InvalidDataException("Snapshot structure invalid.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
