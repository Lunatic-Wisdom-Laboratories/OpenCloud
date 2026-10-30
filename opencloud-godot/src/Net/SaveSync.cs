using System;
using OpenCloud.Game.Save;

namespace OpenCloud.Net;

/// <summary>
/// Host-authoritative replication of a <see cref="GlobalSaveState"/>, independent of the
/// transport. The authority (the host, or an offline game) applies operations, numbers them with
/// a revision and publishes them; every other peer only forwards its operations to the
/// authority and applies what comes back, in revision order. A peer that misses a revision, or
/// whose digest differs from the authority's at the same revision, asks for a snapshot.
/// <see cref="NetworkManager"/> connects the outgoing delegates to its RPCs.
/// </summary>
public sealed class SaveSync
{
    public SaveSync(GlobalSaveState state)
    {
        State = state;
    }

    public GlobalSaveState State { get; }

    /// <summary>Whether this peer owns the state: the host, or an offline game.</summary>
    public bool IsAuthority { get; set; } = true;

    /// <summary>Number of operations the authority has applied since the session's snapshot.</summary>
    public long Revision { get; private set; }

    /// <summary>Whether a snapshot was asked for and has not arrived yet.</summary>
    public bool AwaitingSnapshot { get; private set; }

    /// <summary>Authority side: decides whether a peer may make a change. Null allows every valid operation.</summary>
    public Func<long, SaveOperation, bool>? Authorize { get; set; }

    /// <summary>Peer side: sends an operation to the authority.</summary>
    public Action<SaveOperation>? SendRequest { get; set; }
    /// <summary>Authority side: sends an applied operation to every peer.</summary>
    public Action<long, SaveOperation>? Publish { get; set; }
    /// <summary>Peer side: asks the authority for a snapshot.</summary>
    public Action? SendSnapshotRequest { get; set; }

    /// <summary>Raised on a peer whose state was found to differ from the authority's.</summary>
    public event Action<long>? DesyncDetected;
    /// <summary>Raised after an operation was applied here, on any side.</summary>
    public event Action<SaveOperation, int>? Applied;

    /// <summary>
    /// Asks for a change. The authority applies it at once and gives its result; a peer forwards
    /// it and gives null, and its state changes when the authority's confirmation arrives.
    /// </summary>
    public int? Submit(SaveOperation op)
    {
        if (IsAuthority)
            return ApplyAsAuthority(op);
        SendRequest?.Invoke(op);
        return null;
    }

    /// <summary>Authority side: handles an operation a peer submitted. False when it was refused.</summary>
    public bool HandleRequest(long fromPeer, SaveOperation op)
    {
        if (!IsAuthority || !State.Validate(op))
            return false;
        if (Authorize != null && !Authorize(fromPeer, op))
            return false;
        ApplyAsAuthority(op);
        return true;
    }

    private int ApplyAsAuthority(SaveOperation op)
    {
        if (!State.Validate(op))
            return 0;
        int result = State.Apply(op);
        Revision++;
        Publish?.Invoke(Revision, op);
        Applied?.Invoke(op, result);
        return result;
    }

    /// <summary>Peer side: applies an operation the authority published.</summary>
    public void HandlePublished(long revision, SaveOperation op)
    {
        if (IsAuthority || AwaitingSnapshot)
            return;
        if (revision != Revision + 1)
        {
            RequestSnapshot(revision);
            return;
        }
        int result = State.Apply(op);
        Revision = revision;
        Applied?.Invoke(op, result);
    }

    /// <summary>Peer side: compares the authority's digest with ours.</summary>
    public void HandleHash(long revision, ulong hash)
    {
        if (IsAuthority || AwaitingSnapshot)
            return;
        // Operations travel on the same ordered channel as digests, so a different revision
        // means one went missing.
        if (revision != Revision || hash != SaveStateCodec.Hash(State))
            RequestSnapshot(revision);
    }

    /// <summary>Peer side: replaces the state with the authority's.</summary>
    public void HandleSnapshot(long revision, byte[] snapshot)
    {
        if (IsAuthority)
            return;
        SaveStateCodec.Deserialize(State, snapshot);
        Revision = revision;
        AwaitingSnapshot = false;
    }

    /// <summary>Authority side: the current state and revision, for a peer that joins or asked.</summary>
    public (long Revision, byte[] Snapshot) TakeSnapshot() => (Revision, SaveStateCodec.Serialize(State));

    public ulong Hash() => SaveStateCodec.Hash(State);

    /// <summary>Starts counting revisions afresh, as when a session begins.</summary>
    public void ResetRevision()
    {
        Revision = 0;
        AwaitingSnapshot = false;
    }

    private void RequestSnapshot(long authorityRevision)
    {
        AwaitingSnapshot = true;
        DesyncDetected?.Invoke(authorityRevision);
        SendSnapshotRequest?.Invoke();
    }
}
