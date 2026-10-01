using System;
using System.IO;

namespace ValheimModPack.WorldCharacters
{
    // Protection is selected once per network session and identity. A previously
    // enrolled world must never fall back to the character's older standalone .fch.
    public sealed class LocalProtectionPolicy
    {
        private object network;
        private long world, character;
        private bool decided, protect;
        public bool Resolve(object currentNetwork, long currentWorld, long currentCharacter,
            bool openServer, bool protectNewSoloWorlds, StateStore store)
        {
            // Dedicated servers need no local profile. During startup, do not cache
            // an incomplete identity that could hide an existing protected record.
            if (currentNetwork == null || currentWorld == 0 || currentCharacter == 0)
                return openServer || protectNewSoloWorlds;
            if (decided && ReferenceEquals(network, currentNetwork) && world == currentWorld && character == currentCharacter)
                return protect;
            bool next = openServer || protectNewSoloWorlds || store.HasStoredState(currentWorld, "local-host", currentCharacter);
            network = currentNetwork; world = currentWorld; character = currentCharacter;
            protect = next; decided = true;
            return protect;
        }
        public void Clear() { network = null; world = character = 0; decided = protect = false; }
    }

    public sealed class CharacterSession
    {
        public readonly string Token = Guid.NewGuid().ToString("N");
        public CharacterState State { get; private set; }
        public long LastSequence { get; private set; }
        public bool Loaded { get; private set; }
        public bool Closed { get; private set; }
        private readonly string initialName;
        private CharacterState acceptedState;
        // Staging and commit callbacks belong to the main thread. The disk worker
        // receives independent snapshots and must never mutate this session.
        public CharacterState AcceptedState { get { return acceptedState.Copy(); } }
        public long AcceptedSequence { get; private set; }
        public bool AcceptedFinal { get; private set; }
        public CharacterSession(CharacterState state)
        {
            State = state.Copy(); acceptedState = State.Copy(); initialName = State.Name;
        }
        // Merge against accepted progress without cloning the entire state just
        // to inspect its map. RetainMap returns an independent owned array.
        public byte[] RetainAcceptedMap(byte[] positions)
        {
            return StateCodec.RetainMap(positions, acceptedState.WorldData);
        }
        public void MarkLoaded(string token) { Check(token); Loaded = true; }
        public CharacterState Next(string token, long sequence, CharacterState update)
        {
            Check(token);
            if (!Loaded || sequence != LastSequence + 1) throw new InvalidDataException("Unexpected snapshot sequence or unloaded character.");
            if (update.World != State.World || update.Character != State.Character || update.Owner != State.Owner)
                throw new InvalidDataException("Snapshot identity mismatch.");
            var next = update.Copy(); next.Revision = checked(State.Revision + 1); next.Name = State.Name;
            StateCodec.Validate(next); return next;
        }
        public CharacterState Stage(string token, long sequence, CharacterState update, bool final)
        {
            Check(token);
            if (!Loaded || AcceptedFinal || sequence != checked(AcceptedSequence + 1))
                throw new InvalidDataException("Unexpected staged snapshot sequence, final snapshot or unloaded character.");
            if (update == null || update.World != acceptedState.World || update.Character != acceptedState.Character || update.Owner != acceptedState.Owner)
                throw new InvalidDataException("Snapshot identity mismatch.");
            if (update.Player == null || update.WorldData == null)
                throw new InvalidDataException("Invalid snapshot data.");
            var next = update.Copy(); next.Revision = checked(acceptedState.Revision + 1); next.Name = initialName;
            StateCodec.Validate(next);
            // A failed validation/copy does not reserve a sequence or revision.
            // Keep both the caller and the worker away from the accepted cursor.
            CharacterState accepted = next.Copy();
            acceptedState = accepted; AcceptedSequence = sequence; AcceptedFinal = final;
            return next;
        }
        public void Committed(CharacterState next, long sequence, bool final)
        {
            if (Closed || next == null || next.Revision != checked(State.Revision + 1) || sequence != checked(LastSequence + 1)
                || (final && sequence < AcceptedSequence)) throw new InvalidDataException("Commit order mismatch.");
            CharacterState committed = next.Copy();
            CharacterState accepted = sequence > AcceptedSequence ? committed.Copy() : null;
            State = committed; LastSequence = sequence; Closed = final;
            // Preserve the synchronous Next/Committed path while a staged cursor
            // remains ahead of durability until every queued write completes.
            if (accepted != null) { acceptedState = accepted; AcceptedSequence = sequence; }
            if (final) AcceptedFinal = true;
        }
        private void Check(string token) { if (Closed || token != Token) throw new InvalidDataException("Expired or invalid session token."); }
    }
}
